using System.Windows;
using WinForms = System.Windows.Forms;

namespace EmailClient.UI;

public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Window _window;

    public TrayIcon(Window window, string iconPath)
    {
        _window = window;

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Restore());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconPath),
            Text = "Peacock",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => Restore();
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                Restore();
        };
    }

    public event EventHandler? ExitRequested;

    private void Restore()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ShowBalloon(string title, string text) =>
        _notifyIcon.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
