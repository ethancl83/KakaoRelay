using KakaoRelay.Core;

internal static class WorkspaceChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        WindowSnapshot Window(string title, string handle) => new() { Title = title, Handle = handle, ProcessId = 12, Visible = true, Enabled = true, ClassName = "EVA_Window_Dblclk", NativeChildren = [new("0x200", "RICHEDIT50W", true, true)] };
        var conversation = Window("Recipient", "0x10");
        var main = Window("KakaoTalk", "0x20"); main.NativeChildren.Clear();
        var hidden = Window("Hidden", "0x30"); hidden.Visible = false;
        var minimized = Window("Minimized", "0x40"); minimized.Minimized = true;
        var choices = ConversationCatalog.SelectTargets([conversation, main, hidden, minimized], _ => true);
        check(choices.Count == 1 && choices[0].Title == "Recipient", "Conversation picker excludes main, hidden and minimized windows");
        check(ConversationCatalog.SelectTargets([conversation], _ => false).Count == 0, "Search/edit controls without the message control ID are excluded");
        var duplicate = Window("Recipient", "0x50"); duplicate.Visible = false;
        check(ConversationCatalog.SelectTargets([conversation, duplicate], _ => true)[0].Ambiguous, "A same-name hidden window also blocks ambiguous selection");
        var compose = new ComposeSession();
        bool rejected = false;
        try { compose.Begin(null, "hello", false); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && !compose.Submitted, "Empty recipient cannot create a send request");
        var request = compose.Begin(choices[0], "첫 줄\r\n둘째 줄", false);
        rejected = false;
        try { compose.Begin(choices[0], "another", false); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && compose.Request == request, "Repeated send clicks keep the one compose identity");
        compose.Reset();
        check(compose.Begin(choices[0], "new", false).RequestId != request.RequestId, "Explicit new message creates a separate request");
        var nextId = compose.Request!.RequestId;
        check(!compose.Complete(new() { RequestId = nextId, EnterPosted = true, InputCleared = false }) && compose.Submitted, "Uncertain send retains the draft identity for duplicate protection");
        check(!compose.Complete(new() { RequestId = "another", EnterPosted = true, InputCleared = true }) && compose.Submitted, "Unrelated completion cannot clear the current draft");
        check(compose.Complete(new() { RequestId = nextId, EnterPosted = true, InputCleared = true }) && !compose.Submitted
            && compose.Begin(choices[0], "바로 다음 메시지", true).RequestId != nextId, "Processed send immediately permits the next message without a New Message action");
        check(TestSendRequest.SameText("첫 줄\r\n둘째 줄", "첫 줄\r둘째 줄") && !TestSendRequest.SameText("hello ", "hello"), "RichEdit newline normalization preserves meaningful spaces");
        var ledger = Path.Combine(root, "send-ledger");
        File.WriteAllText(Path.Combine(ledger, "broken.json"), "broken JSON");
        var history = TestSender.ReadHistory(ledger);
        check(history.Any(r => r.Status == "observed-in-chat") && !history.Any(r => r.RequestId == "broken"), "Unified history imports earlier receipts and tolerates a damaged file");
        var blocked = history.First(r => r.Status == "blocked-before-input");
        rejected = false;
        try { TestSender.RecordObserved(blocked.RequestId, ledger); } catch (InvalidOperationException) { rejected = true; }
        check(rejected, "History cannot mark an unsent blocked request as observed");
    }
}
