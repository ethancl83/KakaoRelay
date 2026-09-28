using System.Drawing;
using Forms = System.Windows.Forms;

namespace KakaoRelay.App;

internal sealed class TrayHost : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Icon image;
    private readonly Forms.ToolStripMenuItem exitItem;
    public TrayHost(Action show, Action exit)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/KakaoRelay.ico")).Stream;
        image = new Icon(stream, 32, 32);
        // Queue WPF actions after the native tray menu has finished its click handling.
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        void Open() => dispatcher.BeginInvoke(show);
        void Exit() => dispatcher.BeginInvoke(exit);
        menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("열기", null, (_, _) => Open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        exitItem = new Forms.ToolStripMenuItem("완전 종료", null, (_, _) => Exit());
        menu.Items.Add(exitItem);
        icon = new Forms.NotifyIcon { Icon = image, Text = "KakaoRelay · 실행 중", ContextMenuStrip = menu, Visible = true };
        // NotifyIcon handles right-click and keyboard context-menu requests, even while WPF is hidden.
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Open(); };
    }
    public void SetExiting()
    {
        icon.Text = "KakaoRelay · 작업 완료 후 종료";
        exitItem.Enabled = false;
        exitItem.Text = "종료 중…";
    }
    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        image.Dispose();
    }
}
