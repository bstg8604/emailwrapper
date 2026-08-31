using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EmailClient.Automation;
using EmailClient.Diagnostics;
using EmailClient.Mail;
using EmailClient.Settings;
using EmailClient.UI;

namespace EmailClient;

/// <summary>
/// Composing and sending: reply/forward, the undo-send hold, the undo bar for
/// delete/archive/move, and signature insertion.
/// </summary>

public partial class MainWindow
{
    // ---- Undo send ----------------------------------------------------------------------

    /// <summary>
    /// One message held in its undo window. Compose is now non-modal, so more than one send can
    /// legitimately be in flight at once (send from window A, then send from window B before A's
    /// 5 seconds are up) — a single shared timer/result pair would silently drop the earlier one.
    /// </summary>
    private sealed record PendingSend(ComposeResult Result, DispatcherTimer Timer);

    private readonly List<PendingSend> _pendingSends = [];

    // Gmail's signature feature: sending doesn't happen immediately — it's held for a few
    // seconds with an "Undo" option, then actually goes out once that window passes.
    private void BeginUndoableSend(ComposeResult result)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.ClampedUndoSendSeconds) };
        PendingSend pending = new(result, timer);

        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            _pendingSends.Remove(pending);
            UpdateSendSnackbar();
            await PerformSendAsync(pending.Result);
        };

        _pendingSends.Add(pending);
        timer.Start();
        UpdateSendSnackbar();
    }


    // ---- Undo delete/archive/move --------------------------------------------------------------
    // Same bottom-left snackbar as Undo Send — the two are mutually exclusive in practice (both
    // are rare, fleeting, single-user actions), so one shared slot is simpler than a second corner
    // of the screen for what's functionally the same affordance.

    private sealed record PendingActionUndo(string Label, Func<Task> Undo);

    private PendingActionUndo? _pendingActionUndo;
    private DispatcherTimer? _actionUndoTimer;

    /// <summary>Offers "Undo" for an action that already completed (archive/delete/move) rather
    /// than Send's "delay then do it" — <paramref name="undo"/> reverses whatever already
    /// happened.</summary>
    private void ShowActionUndo(string label, Func<Task> undo)
    {
        _actionUndoTimer?.Stop();
        _pendingActionUndo = new PendingActionUndo(label, undo);

        _actionUndoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.ClampedUndoSendSeconds) };
        _actionUndoTimer.Tick += (_, _) =>
        {
            _actionUndoTimer!.Stop();
            _pendingActionUndo = null;
            UpdateSendSnackbar();
        };
        _actionUndoTimer.Start();
        UpdateSendSnackbar();
    }

    private void UpdateSendSnackbar()
    {
        if (_pendingActionUndo is { } action)
        {
            SendSnackbarText.Text = action.Label;
            if (SendSnackbar.Visibility != Visibility.Visible)
                SendSnackbar.SlideUpReveal();
            return;
        }

        if (_pendingSends.Count == 0)
        {
            if (SendSnackbar.Visibility == Visibility.Visible)
                SendSnackbar.SlideDownHide();
            return;
        }

        SendSnackbarText.Text = _pendingSends.Count == 1 ? "Message sent" : $"{_pendingSends.Count} messages sent";
        if (SendSnackbar.Visibility != Visibility.Visible)
            SendSnackbar.SlideUpReveal();
    }

    // Undoes the most recently sent message, or the most recent archive/delete/move if one of
    // those is what's currently offered — the one a user reaching for "Undo" right after acting
    // almost always means.
    private async void UndoSend_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingActionUndo is { } action)
        {
            _actionUndoTimer?.Stop();
            _pendingActionUndo = null;
            UpdateSendSnackbar();
            await action.Undo();
            return;
        }

        if (_pendingSends.Count == 0)
            return;

        var last = _pendingSends[^1];
        last.Timer.Stop();
        _pendingSends.Remove(last);
        UpdateSendSnackbar();

        OpenCompose(last.Result.To, last.Result.Subject, last.Result.Body, last.Result.Cc, last.Result.Bcc,
            bodyHtml: last.Result.BodyHtml, attachments: last.Result.Files);
    }

    private async Task PerformSendAsync(ComposeResult result)
    {
        try
        {
            if (_mail is null)
            {
                await Task.Delay(200); // simulate the round trip
                AddLocalMessage("Sent", result, "Message sent");
                return;
            }

            StatusText.Text = "Sending…";
            var warnings = await _mail.SendAsync(result);
            StatusText.Text = "Message sent";
            await RefreshMessagesAsync();

            // The send itself succeeded, but something in it didn't (a vanished attachment, a
            // mistyped address) — worth an explicit acknowledgment rather than a status-bar line
            // that's easy to miss, since it affects what the recipient actually received.
            if (warnings.Count > 0)
            {
                System.Windows.MessageBox.Show(this,
                    "Your message was sent, but:\n\n" + string.Join("\n", warnings),
                    "Sent with issues", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            // The compose window is already gone by this point (Undo Send closes it immediately),
            // so a failure here used to just lose whatever the user typed. Reopening it with
            // everything intact — the same call Undo itself uses — means nothing's lost; they can
            // fix whatever's wrong (or just retry) and send again.
            Log.Error("Sending a message failed", ex);
            StatusText.Text = $"Send failed: {MailErrors.Friendly(ex)}";
            OpenCompose(result.To, result.Subject, result.Body, result.Cc, result.Bcc,
                bodyHtml: result.BodyHtml, attachments: result.Files);
        }
    }


    // ---- Reply / forward ------------------------------------------------------------------

    private string SelfAddress => _account?.Email ?? MockData.SelfAddress;

    /// <summary>
    /// True when a sender's domain doesn't match your own — derived from your own signed-in
    /// address rather than a hardcoded organisation domain, so it still makes sense if this app is
    /// ever pointed at a different institution's mail. A subdomain of your own domain still counts
    /// as internal (mail from "cse.iitb.ac.in" when you're "you@iitb.ac.in" isn't "external").
    /// </summary>
    private bool IsExternalSender(string fromAddress)
    {
        var senderDomain = MailText.AddressOnly(fromAddress).Split('@').ElementAtOrDefault(1);
        var ownDomain = SelfAddress.Split('@').ElementAtOrDefault(1);
        if (string.IsNullOrEmpty(senderDomain) || string.IsNullOrEmpty(ownDomain))
            return false;

        return !senderDomain.Equals(ownDomain, StringComparison.OrdinalIgnoreCase)
            && !senderDomain.EndsWith("." + ownDomain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Keeps InboxRow.SelfDomain in sync so the message list can show the same "external
    /// sender" indicator the reading pane shows, without every row needing to know about accounts.</summary>
    private void UpdateSelfDomain() =>
        InboxRow.SelfDomain = SelfAddress.Split('@').ElementAtOrDefault(1) ?? "";

    private static string ReplySubject(string subject) =>
        subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? subject : $"Re: {subject}";

    private static string ForwardSubject(string subject) =>
        subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) ? subject : $"Fwd: {subject}";

    /// <summary>
    /// A reply body with the original message actually quoted below it: an attribution line, then
    /// the original text prefixed with "&gt; ". The leading blank lines are where the user types —
    /// top-posting, the way Gmail and Outlook both open a reply.
    /// </summary>
    private static string BuildReplyBody(MessageDetail detail)
    {
        var original = MailText.HtmlToPlainText(detail.BodyHtml);
        if (string.IsNullOrWhiteSpace(original))
            return "\n\n";

        return $"\n\n{AppleStyleAttribution(detail)}\n\n{MailText.Quote(original)}\n";
    }

    /// <summary>An absolute date for anything baked into a reply/forward's permanent text — see
    /// MailText.FormatAbsoluteDate for why that can't be the same relative "Today"/"Yesterday"
    /// wording detail.Date already carries for the live reading pane header.</summary>
    private static string QuoteDate(MessageDetail detail) =>
        detail.Timestamp is { } ts ? MailText.FormatAbsoluteDate(ts) : detail.Date;

    /// <summary>
    /// Apple Mail's exact quote attribution format: "On Aug 28, 2026, at 5:49 PM, Name
    /// &lt;address&gt; wrote:" — a real absolute date+time (never relative), "at" before the clock
    /// time, and the sender's address in brackets after their name, not just the display name alone.
    /// </summary>
    private static string AppleStyleAttribution(MessageDetail detail)
    {
        var name = MailText.DisplayName(detail.From);
        var address = MailText.AddressOnly(detail.From);
        var who = string.IsNullOrWhiteSpace(name) ? address : $"{name} <{address}>";
        return $"On {ForwardDate(detail)}, {who} wrote:";
    }

    /// <summary>"Aug 28, 2026 at 5:49 PM" — Apple Mail's absolute-date-plus-"at" wording, shared by
    /// the reply attribution and the forwarded-message header's own Date: line.</summary>
    private static string ForwardDate(MessageDetail detail) =>
        detail.Timestamp is { } ts ? ts.ToString("MMM d, yyyy 'at' h:mm tt") : QuoteDate(detail);

    /// <summary>
    /// A forward carries the original's headers as well as its text — a forwarded mail with no
    /// From/Date/Subject block is unreadable to whoever receives it.
    /// </summary>
    private static string BuildForwardBody(MessageDetail detail)
    {
        // Apple Mail's own forward wording and header order: "Begin forwarded message:", then
        // From/Subject/Date/To(/Cc) — not the "---------- Forwarded message ----------" dashes or
        // From/Date/Subject ordering other clients use.
        var header = new List<string>
        {
            "Begin forwarded message:",
            "",
            $"From: {detail.From}",
            $"Subject: {detail.Subject}",
            $"Date: {ForwardDate(detail)}",
        };
        if (!string.IsNullOrWhiteSpace(detail.To))
            header.Add($"To: {detail.To}");
        if (!string.IsNullOrWhiteSpace(detail.Cc))
            header.Add($"Cc: {detail.Cc}");
        if (detail.Attachments is { Count: > 0 } attachments)
            header.Add($"Attachments: {string.Join(", ", attachments.Select(a => a.Name))}");

        return $"\n\n{string.Join("\n", header)}\n\n{MailText.HtmlToPlainText(detail.BodyHtml)}\n";
    }

    private static readonly Regex ImageTagPattern = new(@"<img\b[^>]*>", RegexOptions.IgnoreCase);

    /// <summary>
    /// Quoted originals keep their formatting but lose their images: a remote image in a quote
    /// would phone home the moment the compose window opened, before anything is even sent.
    /// </summary>
    private static string QuotableHtml(string html) => ImageTagPattern.Replace(html, "");

    private static string Encode(string text) => System.Net.WebUtility.HtmlEncode(text);

    private const string QuoteStyle =
        "margin:0 0 0 10px;padding:0 0 0 12px;border-left:3px solid #ddd;color:#555";

    /// <summary>
    /// The HTML counterpart of <see cref="BuildReplyBody"/>: the original goes into a real
    /// blockquote so it keeps its own formatting, rather than being flattened to "&gt; " lines.
    /// </summary>
    private static string BuildReplyBodyHtml(MessageDetail detail)
    {
        return ComposeLeadIn
            + $"<p>{Encode(AppleStyleAttribution(detail))}</p>"
            + $"<blockquote style=\"{QuoteStyle}\">{QuotableHtml(detail.BodyHtml)}</blockquote>";
    }

    private static string BuildForwardBodyHtml(MessageDetail detail)
    {
        var header = new System.Text.StringBuilder();
        header.Append("<p>Begin forwarded message:</p><p><br></p><p>");
        header.Append($"From: {Encode(detail.From)}<br>");
        header.Append($"Subject: {Encode(detail.Subject)}<br>");
        header.Append($"Date: {Encode(ForwardDate(detail))}");
        if (!string.IsNullOrWhiteSpace(detail.To))
            header.Append($"<br>To: {Encode(detail.To)}");
        if (!string.IsNullOrWhiteSpace(detail.Cc))
            header.Append($"<br>Cc: {Encode(detail.Cc)}");
        if (detail.Attachments is { Count: > 0 } attachments)
            header.Append($"<br>Attachments: {Encode(string.Join(", ", attachments.Select(a => a.Name)))}");
        header.Append("</p>");

        return $"{ComposeLeadIn}{header}{QuotableHtml(detail.BodyHtml)}";
    }

    private void ReplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openDetail is not { } detail)
            return;
        OpenCompose(detail.From, ReplySubject(detail.Subject), BuildReplyBody(detail),
            bodyHtml: WithSignature(BuildReplyBodyHtml(detail)));
    }

    private void ReplyAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openDetail is not { } detail)
            return;

        var cc = MailText.MergeRecipients(
            [SelfAddress, MailText.AddressOnly(detail.From)], detail.To, detail.Cc);

        OpenCompose(detail.From, ReplySubject(detail.Subject), BuildReplyBody(detail), cc,
            bodyHtml: WithSignature(BuildReplyBodyHtml(detail)));
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openDetail is not { } detail)
            return;
        OpenCompose("", ForwardSubject(detail.Subject), BuildForwardBody(detail),
            bodyHtml: WithSignature(BuildForwardBodyHtml(detail)));
    }

    /// <summary>Saves the exact RFC 822 source to a temp file and opens it as plain text — the
    /// fastest way to see precisely what a server actually sent when a message isn't rendering the
    /// way it should (mailing-list headers/footers, an unusual MIME structure, etc.), matching
    /// Thunderbird's own "View source" (Ctrl+U).</summary>
    private async void ViewSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openRow is not { } row)
            return;

        string? source;
        if (UseMockData)
        {
            source = _openDetail is { } d
                ? $"Subject: {d.Subject}\nFrom: {d.From}\nDate: {d.Date}\n\n{d.BodyHtml}"
                : null;
        }
        else
        {
            try { source = await _mail!.GetRawSourceAsync(row.Id); }
            catch (Exception) { source = null; }
        }

        if (source is null)
        {
            StatusText.Text = "Couldn't get the message source";
            return;
        }

        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"message-source-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, source);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open the source: {ex.Message}";
        }
    }

    private async void MarkUnreadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openRow is not { } open)
            return;

        if (!UseMockData && !await RunLiveAsync(
                () => _mail!.SetReadAsync(open.Id, false), "Marked as unread", "Couldn't mark as unread"))
            return;

        UpdateRow(open.Id, r => r with { Unread = true });
        UpdateInboxBadge();
        _messagesView.Refresh();
        ResetReadingPane();

        if (!UseMockData)
            await RefreshFoldersAsync();
    }

    private async void ArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openRow is { } open)
            await ArchiveMessageAsync(open);
    }

    /// <summary>
    /// Archiving files the message away — it should still be findable afterwards. On sample data
    /// that means moving it to the Archive folder rather than deleting it outright.
    /// </summary>
    private async Task ArchiveMessageAsync(InboxRow row)
    {
        if (!UseMockData)
        {
            await RemoveMessageAsync(row, id => _mail!.ArchiveAsync(id), "Archived");
            return;
        }

        MoveRowLocally(row.Id, "Archive");
        if (_openRow is not null && _openRow.Id == row.Id)
            ResetReadingPane();
        ApplyCurrentFolderView();
        UpdateInboxBadge();
        StatusText.Text = "Archived (sample data)";
    }

    /// <summary>Sample-data move: takes a row out of whichever folder holds it and files it in another.</summary>
    private bool MoveRowLocally(string id, string folder)
    {
        var row = _folderData.Values.SelectMany(rows => rows).FirstOrDefault(r => r.Id == id)
            ?? _messages.FirstOrDefault(r => r.Id == id);
        if (row is null || !_folderData.TryGetValue(folder, out var target))
            return false;

        RemoveMessageEverywhere(id);
        target.Insert(0, row);
        return true;
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e) =>
        await RemoveOpenMessageAsync(id => _mail!.DeleteAsync(id), "Deleted");

    private async Task RemoveOpenMessageAsync(Func<string, Task<bool>> liveAction, string verb)
    {
        if (_openRow is { } open)
            await RemoveMessageAsync(open, liveAction, verb);
    }

    /// <summary>
    /// Every delete path — the toolbar button, the context menu, the '#'/Delete-key shortcuts —
    /// funnels through this one shared helper (and <see cref="BulkActionAsync"/> for the bulk bar),
    /// so gating on <paramref name="verb"/> here is what makes a confirmation apply to all of them
    /// at once with no risk of a future call site forgetting to ask. Archive and the other verbs
    /// that also go through this same helper are unaffected — losing a message to the wrong folder
    /// is trivially reversible; deleting it isn't quite the same guarantee.
    /// </summary>
    private bool ConfirmDelete(int count)
    {
        var permanent = _currentFolder == "Trash";
        var subject = count == 1 ? "this message" : $"these {count} messages";
        var message = permanent
            ? $"Permanently delete {subject}? This can't be undone."
            : $"Delete {subject}? {(count == 1 ? "It" : "They")} will be moved to Trash.";

        var choice = EmailClient.UI.ConfirmDialog.Show(this, "Delete message" + (count == 1 ? "" : "s"), message,
            warningIcon: true,
            new EmailClient.UI.ConfirmChoice("Cancel"),
            new EmailClient.UI.ConfirmChoice("Delete", Destructive: true));
        return choice == "Delete";
    }

    private async Task RemoveMessageAsync(InboxRow row, Func<string, Task<bool>> liveAction, string verb)
    {
        if (verb == "Deleted" && !ConfirmDelete(1))
            return;

        if (!UseMockData && !await RunLiveAsync(
                () => liveAction(row.Id), verb, $"Couldn't {verb.ToLowerInvariant().TrimEnd('d')} the message"))
            return;

        // Set by ArchiveAsync/DeleteAsync when the server told us the message's new UID in its
        // destination — only then is there anything to actually move back on Undo.
        var undo = UseMockData ? null : _mail!.ConsumeLastMove();

        RemoveMessageEverywhere(row.Id);
        UpdateListEmptyState();
        UpdateInboxBadge();

        if (_openRow is not null && _openRow.Id == row.Id)
            ResetReadingPane();

        if (UseMockData)
        {
            StatusText.Text = $"{verb} (sample data)";
            return;
        }

        if (undo is { } move)
        {
            ShowActionUndo(verb, async () =>
            {
                if (await _mail!.UndoMoveAsync(move))
                    await RefreshMessagesAsync();
                else
                    StatusText.Text = "Couldn't undo — the message may have moved again since";
            });
        }

        await RefreshMessagesAsync();
    }

    // Delete-to-remove is a near-universal mail-client convention (Gmail, Outlook, Apple Mail).
    private async void MessageList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete)
            return;
        if (MessageList.SelectedItem is not InboxRow row)
            return;
        e.Handled = true;
        await RemoveMessageAsync(row, id => _mail!.DeleteAsync(id), "Deleted");
    }


    // ---- Signature --------------------------------------------------------------------------
    // Editing now lives in the account popup's Signature tab (see OpenLoginWindow) rather than a
    // separate window/toolbar button.

    /// <summary>The default signature as HTML, or "" when none is configured.</summary>
    private string SignatureHtml =>
        _settings.DefaultSignatureIndex >= 0 && _settings.DefaultSignatureIndex < _settings.Signatures.Count
            ? _settings.Signatures[_settings.DefaultSignatureIndex].BodyHtml
            : "";

    /// <summary>
    /// The leading blank paragraph a fresh compose or a quoted reply/forward body starts with —
    /// where the signature gets spliced in, right after where the user types and before any quoted
    /// original, matching where Gmail/Outlook place an auto-inserted signature by default. One
    /// blank paragraph, not two — this used to be doubled, reading as two empty lines to type into
    /// before hitting the signature/quote, more than the "just start typing" gap this is meant to be.
    /// </summary>
    private const string ComposeLeadIn = "<p><br></p>";

    /// <summary>Body HTML for a brand-new message: just the signature (if any), above an empty line to type into.</summary>
    private string NewMessageBodyHtml => SignatureHtml.Length == 0 ? "" : $"{ComposeLeadIn}{SignatureHtml}";

    /// <summary>Splices the signature into a reply/forward body that already starts with <see cref="ComposeLeadIn"/>.</summary>
    private string WithSignature(string bodyHtml) =>
        SignatureHtml.Length == 0 || !bodyHtml.StartsWith(ComposeLeadIn, StringComparison.Ordinal)
            ? bodyHtml
            : bodyHtml.Insert(ComposeLeadIn.Length, SignatureHtml);

}
