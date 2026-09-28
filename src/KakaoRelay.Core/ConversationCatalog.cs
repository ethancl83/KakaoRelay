using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KakaoRelay.Core;

public sealed record ConversationTarget(string Title, int ProcessId, string Handle, bool Ambiguous)
{
    public string Hint => Ambiguous ? "같은 이름의 방이 여러 개 있어 선택할 수 없습니다" : "열린 대화방";
    public bool Selectable => !Ambiguous;
}

public static class ConversationCatalog
{
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(nint handle);
    public static List<ConversationTarget> Scan()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("KakaoTalk"))
            using (process)
                try { if (process.SessionId == Process.GetCurrentProcess().SessionId) ids.Add(process.Id); }
                catch (InvalidOperationException) { }
        return SelectTargets(NativeWindows.Enumerate(ids), child => GetDlgCtrlID(NativeWindows.Parse(child.Handle)) == 1006);
    }
    public static List<ConversationTarget> SelectTargets(IEnumerable<WindowSnapshot> windows, Func<NativeControlSnapshot, bool> isMessageEditor)
    {
        var all = windows.ToList();
        return all.Where(w => w.Visible && !w.Minimized && w.Enabled && w.ClassName == "EVA_Window_Dblclk" && !string.IsNullOrWhiteSpace(w.Title)
            && w.NativeChildren.Count(c => c.Visible && c.Enabled && c.ClassName == "RICHEDIT50W" && isMessageEditor(c)) == 1)
            .Select(w => new ConversationTarget(w.Title, w.ProcessId, w.Handle, all.Count(other => other.Title == w.Title) != 1))
            .OrderBy(w => w.Title, StringComparer.CurrentCulture).ToList();
    }
}

public sealed class ComposeSession
{
    public TestSendRequest? Request { get; private set; }
    public bool Submitted => Request is not null;
    public TestSendRequest Begin(ConversationTarget? target, string message, bool verifiedPlaceholder)
    {
        if (Submitted) throw new InvalidOperationException("This compose session was already submitted");
        if (target is null || !target.Selectable) throw new InvalidOperationException("Select an unambiguous conversation");
        var request = new TestSendRequest($"app-{Guid.NewGuid():N}", target.Title, message, target.ProcessId, target.Handle, DateTimeOffset.Now.AddMinutes(5), verifiedPlaceholder);
        request.Validate();
        Request = request;
        return request;
    }
    public void Reset() => Request = null;
    public bool Complete(TestSendReceipt receipt)
    {
        if (Request?.RequestId != receipt.RequestId || !receipt.EnterPosted || receipt.InputCleared != true) return false;
        Reset();
        return true;
    }
}
