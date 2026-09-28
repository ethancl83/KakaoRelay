using System.Runtime.InteropServices;
using System.Text;

namespace KakaoRelay.Core;

internal static class NativeWindows
{
    private delegate bool EnumWindowProc(nint handle, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();

    internal static string Format(nint handle) => $"0x{handle:X}";
    internal static nint Parse(string value) => (nint)long.Parse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber);
    private static string ClassName(nint handle) { var result = new StringBuilder(256); GetClassName(handle, result, result.Capacity); return result.ToString(); }

    internal static List<WindowSnapshot> Enumerate(HashSet<int> processIds)
    {
        var result = new List<WindowSnapshot>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var id);
            if (!processIds.Contains((int)id)) return true;
            var title = new StringBuilder(1024);
            // Only top-level captions; never request child edit text or chat contents.
            GetWindowText(handle, title, title.Capacity);
            var window = new WindowSnapshot { Handle = Format(handle), ProcessId = (int)id, Title = title.ToString(), ClassName = ClassName(handle), Visible = IsWindowVisible(handle), Enabled = IsWindowEnabled(handle), Minimized = IsIconic(handle) };
            EnumChildWindows(handle, (child, _) =>
            {
                GetWindowThreadProcessId(child, out var childId);
                if (childId == id) window.NativeChildren.Add(new(Format(child), ClassName(child), IsWindowVisible(child), IsWindowEnabled(child)));
                return window.NativeChildren.Count < 512;
            }, 0);
            if (window.NativeChildren.Count == 512) window.Warnings.Add("Win32 자식 창 탐색이 512개 제한에 도달했습니다.");
            result.Add(window);
            return result.Count < 64;
        }, 0);
        return result.OrderByDescending(w => w.Visible).ThenBy(w => w.Minimized).ToList();
    }
}
