using System.Windows;
using EmailClient.Mail;
using EmailClient.UI;

namespace EmailClient;

public partial class MainWindow
{
    private bool _scheduledSendInFlight;

    /// <summary>
    /// Checked every 30s (see the timer set up in SwitchToLiveAsync) and once immediately on
    /// sign-in, so a message scheduled for a time the app happened to be closed/offline through
    /// still goes out as soon as it's running again, rather than waiting for the next tick.
    /// </summary>
    private async Task SendDueScheduledMessagesAsync()
    {
        if (_scheduledSends is null)
            return;

        // Guards the same shape of bug RefreshMessagesAsync's own _refreshInFlight already guards
        // against: a slow send (a stalled SMTP connection) can easily run past the 30s tick
        // interval, and without this an overlapping tick would re-`Load()` the queue and find the
        // very messages the first tick's own `due` list already captured but hasn't reached yet in
        // its foreach — sending each of those twice, once from each tick.
        if (_scheduledSendInFlight)
            return;

        var due = _scheduledSends.Load().Where(s => s.SendAtUtc <= DateTime.UtcNow).ToList();
        if (due.Count == 0)
            return;

        _scheduledSendInFlight = true;
        try
        {
            foreach (var scheduled in due)
            {
                // Removed before sending, not after — PerformSendAsync already reopens the compose
                // window with everything intact if the send itself fails, so leaving this entry queued
                // here too on failure would just double that same message into two independent
                // recovery paths instead of one.
                _scheduledSends.Remove(scheduled.Id);
                await PerformSendAsync(scheduled.Result);
            }
            UpdateScheduledBadge();
        }
        finally
        {
            _scheduledSendInFlight = false;
        }
    }

    /// <summary>Same badge pattern as Drafts' own count — hidden entirely at zero rather than
    /// showing a "0".</summary>
    private void UpdateScheduledBadge()
    {
        var count = _scheduledSends?.Load().Count ?? 0;
        ScheduledCount.Text = count.ToString();
        ScheduledBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Not a real folder to navigate into — a lightweight dropdown listing what's queued,
    /// since a full message-list-style view for what's usually zero-to-a-handful of entries would
    /// be a lot of UI for very little content. Clicking an entry cancels it (with confirmation);
    /// there's no "open/edit" here since a scheduled message can't be partially re-edited without
    /// reopening it as a fresh compose, which isn't worth the added complexity for how rarely
    /// someone would want to tweak (rather than just cancel and start over) a scheduled send.</summary>
    private void FolderScheduled_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var items = (_scheduledSends?.Load() ?? []).OrderBy(s => s.SendAtUtc).ToList();
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = FolderScheduled };

        if (items.Count == 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Nothing scheduled", IsEnabled = false });
        }
        else
        {
            foreach (var scheduled in items)
            {
                var to = string.IsNullOrWhiteSpace(scheduled.Result.To) ? "(no recipient)" : scheduled.Result.To;
                var subject = string.IsNullOrWhiteSpace(scheduled.Result.Subject) ? "(no subject)" : scheduled.Result.Subject;
                var when = scheduled.SendAtUtc.ToLocalTime().ToString("ddd, MMM d 'at' h:mm tt");
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = $"{subject} — to {to} — sends {when}",
                    ToolTip = "Click to cancel this scheduled send",
                };
                item.Click += (_, _) => CancelScheduledSend(scheduled.Id, subject);
                menu.Items.Add(item);
            }
        }

        menu.Opened += (_, _) => menu.PopIn();
        menu.IsOpen = true;
    }

    private void CancelScheduledSend(string id, string subject)
    {
        if (_scheduledSends is null)
            return;
        if (ConfirmDialog.Show(this, "Cancel scheduled send",
                $"Cancel sending \"{subject}\"? It will be discarded — there's no way to recover it afterward.",
                warningIcon: true, new ConfirmChoice("Keep it"), new ConfirmChoice("Cancel send", Destructive: true))
            != "Cancel send")
            return;

        if (_scheduledSends.Remove(id))
        {
            StatusText.Text = "Scheduled send cancelled";
            UpdateScheduledBadge();
        }
    }
}
