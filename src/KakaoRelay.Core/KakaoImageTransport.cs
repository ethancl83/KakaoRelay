using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace KakaoRelay.Core;

// Native file drop and a strictly identified, newly opened attachment confirmation window.
// Never changes the clipboard, foreground window, or physical keyboard focus.
public sealed class KakaoImageTransport : IImageSendTransport
{
    private TestSendRequest? request;
    private HashSet<string> before = [];
    private readonly KakaoTestTransport targetCheck = new();
    private static readonly HashSet<string> PreviewTitles = ["사진 전송", "사진 보내기", "이미지 전송", "이미지 보내기", "파일 전송", "파일 보내기"];
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(nint window);
    public void Validate(TestSendRequest desired)
    {
        targetCheck.ValidateTarget(desired);
        if (!desired.IsEmptyDraft(targetCheck.ReadDraft())) throw new InvalidOperationException("카카오톡의 작성 중인 초안을 먼저 정리하세요.");
        request = desired;
        before = NativeWindows.Enumerate([desired.ExpectedProcessId]).Select(w => w.Handle).ToHashSet();
    }
    public void Attach(string path)
    {
        if (request is null) throw new InvalidOperationException("이미지 대상이 확인되지 않았습니다.");
        targetCheck.ValidateTarget(request);
        if (!request.IsEmptyDraft(targetCheck.ReadDraft())) throw new InvalidOperationException("이미지 첨부 전에 다른 초안이 생겼습니다.");
        var names = Encoding.Unicode.GetBytes(Path.GetFullPath(path) + "\0\0");
        var bytes = new byte[20 + names.Length]; // DROPFILES, followed by a double-NUL UTF-16 list.
        BitConverter.GetBytes(20).CopyTo(bytes, 0);
        BitConverter.GetBytes(1).CopyTo(bytes, 16); // fWide
        names.CopyTo(bytes, 20);
        var memory = GlobalAlloc(0x42, (nuint)bytes.Length);
        if (memory == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var posted = false;
        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(memory); }
            posted = PostMessageW(NativeWindows.Parse(request.ExpectedWindowHandle), 0x0233, (nuint)memory, 0);
            if (!posted) throw new Win32Exception(Marshal.GetLastWin32Error(), "파일 첨부 요청 실패");
            // After WM_DROPFILES is queued, KakaoTalk owns HDROP and releases it with DragFinish.
        }
        finally { if (!posted) GlobalFree(memory); }
    }
    public bool ConfirmPreview()
    {
        if (request is null) throw new InvalidOperationException("이미지 대상이 확인되지 않았습니다.");
        WindowSnapshot? preview = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            Thread.Sleep(100);
            var candidates = NativeWindows.Enumerate([request.ExpectedProcessId]).Where(w => w.Visible && w.Enabled && !before.Contains(w.Handle)
                && IsAttachmentDialog(w) && OwnedByTarget(NativeWindows.Parse(w.Handle))).ToList();
            if (candidates.Count > 1) throw new InvalidOperationException("이미지 전송 창이 여러 개입니다. 자동 확인을 중단했습니다.");
            if (candidates.Count == 1) { preview = candidates[0]; break; }
        }
        if (preview is null) throw new InvalidOperationException("첨부 요청 후 이 대화방의 이미지 전송 창을 확인하지 못했습니다. 대화를 확인하고 다시 보내지 마세요.");
        var handle = NativeWindows.Parse(preview.Handle);
        NativeWindows.GetWindowThreadProcessId(handle, out var pid);
        if (pid != request.ExpectedProcessId || !OwnedByTarget(handle) || new[] { 0x10, 0x11, 0x12 }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
            throw new InvalidOperationException("이미지 전송 창 또는 키보드 상태가 바뀌었습니다.");
        if (!PostMessageW(handle, 0x0100, 0x0D, 0x001C0001)) throw new Win32Exception(Marshal.GetLastWin32Error(), "이미지 전송 Enter 실패");
        if (!PostMessageW(handle, 0x0101, 0x0D, unchecked((nint)0xC01C0001))) throw new InvalidOperationException("이미지 전송 요청 결과가 불확실합니다. 재전송하지 마세요.");
        for (var attempt = 0; attempt < 50; attempt++)
        {
            Thread.Sleep(100);
            if (!NativeWindows.Enumerate([request.ExpectedProcessId]).Any(w => w.Handle == preview.Handle && w.Visible)) return true;
        }
        return false;
    }
    private static bool IsAttachmentDialog(WindowSnapshot window) => window.ClassName == "EVA_Window_Dblclk"
        && (window.Title.Length == 0 || PreviewTitles.Contains(window.Title))
        && window.NativeChildren.Any(c => c.ClassName == "EVA_ChildWindow" && c.Visible && c.Enabled && GetDlgCtrlID(NativeWindows.Parse(c.Handle)) == 1001)
        && window.NativeChildren.Any(c => c.ClassName == "EVA_ChildWindow_Dblclk" && GetDlgCtrlID(NativeWindows.Parse(c.Handle)) == 1002)
        && !window.NativeChildren.Any(c => c.ClassName == "RICHEDIT50W");
    private bool OwnedByTarget(nint window)
    {
        var target = NativeWindows.Parse(request!.ExpectedWindowHandle);
        for (var depth = 0; depth < 8 && window != 0; depth++) { window = GetWindow(window, 4); if (window == target) return true; }
        return false;
    }
}
