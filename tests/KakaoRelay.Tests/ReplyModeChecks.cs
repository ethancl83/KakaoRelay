using KakaoRelay.Core;
using System.Collections.Concurrent;

internal static class ReplyModeChecks
{
    private sealed class Reader : IChatReader
    {
        public LocalRoom Room = new("test", "1", "테스트", true);
        public Task<List<LocalRoom>> RoomsAsync(CancellationToken cancellation = default) => Task.FromResult(new List<LocalRoom>{Room});
        public Task<ChatContext> ReadAsync(string profile,string room,int limit,CancellationToken cancellation=default) => Task.FromResult(new ChatContext(Room,
            [new LocalMessage(limit==1 ? "1" : "2","2","사람",DateTimeOffset.Now,1,"호출어 없는 대화",false)],DateTimeOffset.Now));
    }
    private sealed class Runner : IAiRunner
    {
        public ConcurrentBag<string> Calls = [];
        public bool FailGrok, Invalid;
        public async Task<string> RunAsync(AiProviderSettings p, string prompt, int seconds, CancellationToken ct)
        {
            Calls.Add(p.Id + ":" + p.Model + ":" + p.Effort);
            await Task.Delay(p.Id == "grok" ? 10 : 60, ct);
            if (FailGrok && p.Id == "grok") throw new InvalidOperationException("offline");
            return Invalid ? "unclear" : "SKIP";
        }
    }
    public static async Task RunAsync(string root, Action<bool,string> check)
    {
        var settings = new AiSettings { PollSeconds = 1, ReplyMode = "context" };
        var store = new AiSettingsStore(Path.Combine(root, "reply-mode.json")); store.Save(settings);
        check(store.Load().PollSeconds == 1 && store.Load().ReplyMode == "context", "Polling interval and reply mode persist");
        settings.RoomReplyModes[AiSettings.RoomKey("one", "10")] = "immediate";
        settings.RoomReplyModes[AiSettings.RoomKey("two", "10")] = "trigger";
        store.Save(settings); var restored = store.Load();
        check(restored.ReplyModeForRoom("one", "10") == "immediate" && restored.ReplyModeForRoom("two", "10") == "trigger" && restored.ReplyModeForRoom("one", "11") == "context", "Room reply modes persist independently by profile and room; legacy rooms retain their previous mode");
        var invalidModes = settings.Copy(); invalidModes.RoomReplyModes[AiSettings.RoomKey("one", "10")] = "bad";
        try { invalidModes.Validate(); check(false, "invalid room mode"); } catch (ArgumentException) { check(true, "Invalid room reply modes cannot be saved"); }
        foreach(var invalid in new[]{0,301}) { settings.PollSeconds=invalid; try { settings.Validate(); check(false,"invalid poll"); } catch(ArgumentException) { check(true,"Reject polling interval outside 1–300 seconds"); } }
        settings.PollSeconds=1;
        var messages=Enumerable.Range(1,25).Select(i=>new LocalMessage(i.ToString(),"2","user",DateTimeOffset.Now,1,"message-"+i,false)).ToArray();
        var prompt=ContextReplyJudge.Prompt(settings.Persona,messages);
        check(!prompt.Contains("message-5\"") && prompt.Contains("message-6\"") && prompt.Contains("message-25\""),"Context judge receives exactly the most recent 20 messages");
        var runner=new Runner(); var judge=new ContextReplyJudge(runner);
        check(!await judge.ShouldReplyAsync(settings,settings.Persona,messages,default) && judge.PreferredProvider=="grok", "Fastest valid judgment selects Grok and honors SKIP");
        var count=runner.Calls.Count;
        await judge.ShouldReplyAsync(settings,settings.Persona,messages,default);
        check(runner.Calls.Count==count+1 && runner.Calls.All(s=>s is "codex:gpt-6-luna:low" or "grok:grok-4.7:low"),"Subsequent judgments reuse the selected exact model with low effort");
        runner.FailGrok=true;
        await judge.ShouldReplyAsync(settings,settings.Persona,messages,default);
        check(judge.PreferredProvider=="codex","Failed preferred judgment falls back to the other model");
        runner.Invalid=true;
        try { await judge.ShouldReplyAsync(settings,settings.Persona,messages,default); check(false,"invalid judgment"); } catch(InvalidOperationException) { check(true,"Invalid judgments cannot authorize a reply"); }
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        try { await judge.ShouldReplyAsync(settings,settings.Persona,messages,cancelled.Token); check(false,"cancel"); } catch(OperationCanceledException) { check(true,"Cancelled judgment does not return a reply decision"); }
        var candidates=messages.Append(messages[^1] with {Id="26",AuthorId="1"}).Append(messages[^1] with{Id="27",Deleted=true});
        check(AutoReplySession.Candidates(candidates,"24","1","").Select(m=>m.Id).SequenceEqual(new[]{"25"}),"Immediate mode accepts new text while excluding self and deleted messages");
        check(AutoReplySession.Candidates(messages,"24","1","@bot").Count==0,"Trigger mode ignores non-trigger messages");
        var reader=new Reader(); var sends=new List<ApiSendCommand>();
        var service=new AiService(reader,store,new Runner());
        using(var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var session=new AutoReplySession(service,c=>{sends.Add(c);return Task.FromResult(new TestSendReceipt{EnterPosted=true,InputCleared=true});});
            session.StatusChanged+=s=>{if(s.Contains("맥락상 답변 생략"))stop.Cancel();};
            try { await session.RunAsync(reader.Room,"1","",stop.Token); } catch(OperationCanceledException) { }
            check(sends.Count==0 && service.ReplyJudge.PreferredProvider is not null,"Context SKIP sends no messages");
        }
        settings.RoomReplyModes[AiSettings.RoomKey(reader.Room.Profile, reader.Room.Id)]="immediate"; store.Save(settings);
        using(var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var session=new AutoReplySession(service,c=>{sends.Add(c);stop.Cancel();return Task.FromResult(new TestSendReceipt{EnterPosted=true,InputCleared=true});});
            try { await session.RunAsync(reader.Room,"1","",stop.Token); } catch(OperationCanceledException) { }
            check(sends.Count==1 && sends[0].Message=="SKIP" && sends[0].RequestId.StartsWith("bot-"),"Room-specific immediate mode overrides the global context mode and sends only the generated answer");
        }
    }
}
