using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KakaoRelay.Core;

// Experimental transport for an already-open, visually verified conversation only.
// No foreground activation, clipboard, physical keyboard input, or automatic retry.
public sealed class KakaoTestTransport : ITestSendTransport
{
    private nint editor;
    private nint target;
    private TestSendRequest? request;
    public string ForegroundWindow => NativeWindows.Format(NativeWindows.GetForegroundWindow());

    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(nint handle);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint handle, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendText(nint handle, uint message, nuint wParam, string value, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint ReadText(nint handle, uint message, nuint wParam, StringBuilder value, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern nint SendNumber(nint handle, uint message, nuint wParam, nint value, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder data, int bytes, out int needed);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint handle);

    public void ValidateTarget(TestSendRequest desired)
    {
        desired.Validate();
        RequireUnlockedDesktop();
        using var process = Process.GetProcessById(desired.ExpectedProcessId);
        if (process.ProcessName != "KakaoTalk" || process.SessionId != Process.GetCurrentProcess().SessionId) throw new InvalidOperationException("Target process or session changed");
        var matches = NativeWindows.Enumerate([desired.ExpectedProcessId]).Where(w => w.Title == desired.Recipient).ToList();
        if (matches.Count != 1) throw new InvalidOperationException("Recipient window is missing or ambiguous");
        var window = matches[0];
        if (window.Handle != desired.ExpectedWindowHandle || window.ClassName != "EVA_Window_Dblclk" || !window.Visible || window.Minimized || !window.Enabled)
            throw new InvalidOperationException("Observed conversation identity or state changed");
        var candidates = window.NativeChildren.Where(c => c.ClassName == "RICHEDIT50W" && c.Enabled && c.Visible && GetDlgCtrlID(NativeWindows.Parse(c.Handle)) == 1006).ToList();
        if (candidates.Count != 1) throw new InvalidOperationException("Expected one message editor with control ID 1006");
        var currentEditor = NativeWindows.Parse(candidates[0].Handle);
        if (editor != 0 && editor != currentEditor) throw new InvalidOperationException("Message editor was replaced");
        target = NativeWindows.Parse(window.Handle);
        editor = currentEditor;
        request = desired;
        CheckIdentity();
    }

    public string ReadDraft()
    {
        CheckIdentity();
        if (SendNumber(editor, 0x000E, 0, 0, 0x0002, 2000, out var count) == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Editor length query timed out or failed");
        var buffer = new StringBuilder(checked((int)count + 1));
        if (ReadText(editor, 0x000D, (nuint)buffer.Capacity, buffer, 0x0002, 2000, out _) == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Editor read timed out or failed");
        return buffer.ToString();
    }

    public void WriteDraft(string message)
    {
        if (request is null) throw new InvalidOperationException("Target was not validated");
        ValidateTarget(request);
        if (!request.AcceptsInitialDraft(ReadDraft())) throw new InvalidOperationException("A different draft appeared before input");
        // WM_SETTEXT does not notify EN_CHANGE for multiline edits. Replace the selection
        // through the edit operation so KakaoTalk can update its send-button state.
        if (SendNumber(editor, 0x00B1, 0, -1, 0x0002, 2000, out _) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Selecting the verified test draft failed");
        if (SendText(editor, 0x00C2, 1, message, 0x0002, 2000, out _) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Text input failed or is uncertain");
    }

    public void PostEnter()
    {
        if (request is null) throw new InvalidOperationException("Target was not validated");
        ValidateTarget(request);
        if (!TestSendRequest.SameText(ReadDraft(), request.Message)) throw new InvalidOperationException("Draft changed before Enter");
        if (new[] { 0x10, 0x11, 0x12 }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0)) throw new InvalidOperationException("A keyboard modifier is held; Enter was not sent");
        if (!PostMessageW(editor, 0x0100, 0x0D, 0x001C0001)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Enter key-down was not queued");
        if (!PostMessageW(editor, 0x0101, 0x0D, unchecked((nint)0xC01C0001))) throw new Win32Exception(Marshal.GetLastWin32Error(), "Enter key-up is uncertain; do not resend");
    }

    private void CheckIdentity()
    {
        if (request is null || editor == 0 || !NativeWindows.IsWindow(target) || !NativeWindows.IsWindow(editor) || GetAncestor(editor, 2) != target)
            throw new InvalidOperationException("Window or editor no longer exists");
        NativeWindows.GetWindowThreadProcessId(editor, out var id);
        if (id != request.ExpectedProcessId) throw new InvalidOperationException("Editor process changed");
    }

    private static void RequireUnlockedDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) throw new InvalidOperationException("Interactive desktop is unavailable or locked");
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) || name.ToString() != "Default")
                throw new InvalidOperationException("Default interactive desktop is unavailable");
        }
        finally { CloseDesktop(desktop); }
    }
}
