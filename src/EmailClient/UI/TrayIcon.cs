using System.IO;
using EmailClient.Diagnostics;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace EmailClient.UI;

/// <summary>
/// The tray presence: the icon itself, the window's hide/restore behaviour, and the new-mail
/// notifications. On Windows 10 and 11 a NotifyIcon balloon is rendered by the shell as a real
/// toast (and lands in the Action Center), so this is a desktop notification rather than the old
/// XP-style balloon the API name suggests — and it needs no packaged identity or extra dependency.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Window _window;

    /// <summary>
    /// The window state to come back to. Restoring always to Normal (what this used to do) quietly
    /// un-maximized a maximized window every time it went to the tray and back.
    /// </summary>
    private WindowState _restoreTo = WindowState.Normal;

    /// <summary>
    /// The state <see cref="Restore"/> comes back to. Settable because a start-in-tray launch hides
    /// the window while it's still Minimized, so there's no meaningful state to capture from it —
    /// the caller supplies the one the window would otherwise have opened at.
    /// </summary>
    public WindowState RestoreState
    {
        get => _restoreTo;
        set => _restoreTo = value == WindowState.Minimized ? WindowState.Normal : value;
    }

    /// <summary>
    /// Identifies whatever the current notification is about, so a click can open that message.
    /// Cleared once consumed — a stale click (the toast expired, the Action Center entry is from an
    /// hour ago) shouldn't reopen a message the user has since dealt with.
    /// </summary>
    private string? _notificationTarget;

    public TrayIcon(Window window, string iconPath)
    {
        _window = window;

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFresh());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(iconPath),
            Text = "Purplemail",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => RestoreFresh();
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                RestoreFresh();
        };
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            var target = _notificationTarget;
            _notificationTarget = null;
            Restore();
            if (target is not null)
                NotificationOpened?.Invoke(this, target);
        };
    }

    public event EventHandler? ExitRequested;

    /// <summary>Raised with the message id when the user clicks a new-mail notification.</summary>
    public event EventHandler<string>? NotificationOpened;

    /// <summary>
    /// Falls back to the executable's own icon if icon.ico is missing or corrupt. This runs during
    /// window load, and an exception here used to take out the tray icon, the hide-to-tray path,
    /// and the rest of Loaded with it — leaving an app that can't be closed without Task Manager.
    /// </summary>
    private static System.Drawing.Icon LoadIcon(string iconPath)
    {
        try
        {
            if (File.Exists(iconPath))
                return new System.Drawing.Icon(iconPath);
        }
        catch (Exception ex)
        {
            // Fall through to the extracted/default icon below.
            Log.Warn($"Couldn't load the tray icon from '{iconPath}'", ex);
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } extracted)
                return extracted;
        }
        catch (Exception)
        {
            // Fall through.
        }

        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>Sends the window to the tray, remembering the state to restore it to.</summary>
    public void HideToTray()
    {
        if (_window.WindowState != WindowState.Minimized)
            _restoreTo = _window.WindowState;
        _window.Hide();
    }

    public void Restore()
    {
        _window.Show();
        _window.WindowState = _restoreTo;
        _window.Activate();
    }

    /// <summary>Raised after a plain restore (icon click/double-click, or the context menu's
    /// "Open") — not after clicking a new-mail notification, which already has its own explicit
    /// "go to this message" behaviour via <see cref="NotificationOpened"/> that this would
    /// otherwise immediately undo.</summary>
    public event EventHandler? RestoredFresh;

    /// <summary>The "bring the app back" gesture someone actually reaches for the tray icon for —
    /// as opposed to picking up exactly where a specific message or draft was left, restoring here
    /// means landing back on the Inbox, nothing open, scrolled to the top, same as a fresh
    /// launch would. The window itself was never destroyed (that's the whole point of living in
    /// the tray — the live IMAP connection survives), so without this, "restore" would otherwise
    /// silently mean "resume exactly where I left off," which read as the app never having
    /// actually reset at all.</summary>
    public void RestoreFresh()
    {
        Restore();
        RestoredFresh?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts the unread count on the hover tooltip, so the tray icon says something while the
    /// window is hidden. Windows silently drops a tooltip over 63 characters.
    /// </summary>
    public void SetUnread(int unread)
    {
        var text = unread > 0 ? $"Purplemail — {unread} unread" : "Purplemail";
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    /// <summary>
    /// Shows a desktop notification. <paramref name="targetId"/> is the message a click should
    /// open, if any.
    /// </summary>
    public void Notify(string title, string text, string? targetId = null)
    {
        _notificationTarget = targetId;
        _notifyIcon.ShowBalloonTip(8000, title, text, WinForms.ToolTipIcon.Info);
    }

    public void ShowBalloon(string title, string text) => Notify(title, text);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
