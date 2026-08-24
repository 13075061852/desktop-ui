using System.Drawing;
using Forms = System.Windows.Forms;

namespace DeskNest.App.Services;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;

    public TrayService(Action show, Action organize, Action toggleIcons, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示栖格", null, (_, _) => show());
        menu.Items.Add("一键整理", null, (_, _) => organize());
        menu.Items.Add("显示/隐藏原图标", null, (_, _) => toggleIcons());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "栖格 · DeskNest",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => show();
    }

    public void ShowMessage(string title, string message)
    {
        _notifyIcon.ShowBalloonTip(2500, title, message, Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }
}
