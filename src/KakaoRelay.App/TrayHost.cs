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
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("KakaoRelay 열기", null, (_, _) => show());
        menu.Items.Add(new Forms.ToolStripSeparator());
        exitItem = new Forms.ToolStripMenuItem("완전 종료", null, (_, _) => exit());
        menu.Items.Add(exitItem);
        icon = new Forms.NotifyIcon { Icon = image, Text = "KakaoRelay · 실행 중", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
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
