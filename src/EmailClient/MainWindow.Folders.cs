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
/// The folder sidebar: listing and switching folders, dragging a message onto one, and the
/// "Move to..." menu.
/// </summary>

public partial class MainWindow
{
    // ---- Folders ------------------------------------------------------------------------

    private async void FolderInbox_Click(object sender, MouseButtonEventArgs e) => await GoToFolderAsync("Inbox");
    private async void FolderSent_Click(object sender, MouseButtonEventArgs e) => await GoToFolderAsync("Sent");
    private async void FolderDrafts_Click(object sender, MouseButtonEventArgs e) => await GoToFolderAsync("Drafts");
    private async void FolderArchive_Click(object sender, MouseButtonEventArgs e) => await GoToFolderAsync("Archive");
    private async void FolderTrash_Click(object sender, MouseButtonEventArgs e) => await GoToFolderAsync("Trash");
    private async void FolderStarred_Click(object sender, MouseButtonEventArgs e) => await GoToFolderAsync("Starred");

    private async void LiveFolder_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Border { Tag: string mailbox })
            await GoToFolderAsync(mailbox);
    }

    // Matches MainWindow.xaml's FolderActiveBrush (#FFE7D9F7) — deliberately stronger than
    // FolderRow's hover tint (#FFF0EAFA) so "you're in this folder" reads as the dominant cue.
    private static readonly System.Windows.Media.Brush ActiveFolderBrush =
        new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE7, 0xD9, 0xF7));

    private void HighlightFolder(string folder)
    {
        var fixedRows = new Dictionary<string, System.Windows.Controls.Border>
        {
            ["Inbox"] = FolderInbox,
            ["Sent"] = FolderSent,
            ["Drafts"] = FolderDrafts,
            ["Archive"] = FolderArchive,
            ["Trash"] = FolderTrash,
            ["Starred"] = FolderStarred,
        };

        // A local Background value — even Transparent — always outranks the FolderRow style's
        // IsMouseOver trigger, so stamping Transparent on every inactive row here used to kill
        // their hover highlight entirely. Clearing the local value instead lets the style's own
        // Transparent default (and its hover trigger) take back over.
        foreach (var (name, border) in fixedRows)
        {
            if (name == folder)
                border.Background = ActiveFolderBrush;
            else
                border.ClearValue(System.Windows.Controls.Border.BackgroundProperty);
        }

        if (!UseMockData)
            RenderLiveFolders();
    }

    private async Task GoToFolderAsync(string folder)
    {
        // "Starred" is this app's idea, not a real mailbox — express it the way IMAP servers do:
        // the flagged messages of a real folder (Inbox).
        if (!UseMockData && folder == "Starred")
        {
            await GoToFolderAsync("Inbox");
            FilterStarred.IsChecked = true;
            FilterAll.IsChecked = false;
            _quickFilter = "Starred";
            _messagesView.Refresh();
            UpdateListEmptyState();
            HighlightFolder("Starred");
            StatusText.Text = "Starred messages in Inbox";
            return;
        }

        _currentFolder = folder;
        HighlightFolder(folder);
        ResetReadingPane();
        ClearSelection();
        ResetQuickFilter();

        // A search result set is specific to the folder it was run in — leaving it showing after
        // switching folders would mean the list is silently still filtered to a different folder's
        // search entirely.
        if (_serverSearchActive)
        {
            _serverSearchActive = false;
            SearchBox.Text = "";
        }

        if (_mail is null)
        {
            LoadMockFolder(folder);
            return;
        }

        var mailbox = SpecialMailbox(folder);
        StatusText.Text = $"Opening {FolderDisplayName(folder)}…";
        try
        {
            if (!await _mail.SelectFolderAsync(mailbox))
            {
                StatusText.Text = $"Couldn't open {FolderDisplayName(folder)} — it may no longer exist";
                return;
            }
            _mail.StartIdleMonitor(mailbox);
            await RefreshMessagesAsync();
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open {FolderDisplayName(folder)}: {MailErrors.Friendly(ex)}";
        }
    }

    private string FolderDisplayName(string folder) =>
        _liveFolders.FirstOrDefault(f => f.Mailbox == folder)?.Name ?? folder;

    private void ResetQuickFilter()
    {
        // A leftover "Unread"/"Starred" quick filter from the previous folder could make the
        // new folder look empty for no visible reason — quick filters reset per folder.
        _quickFilter = "All";
        FilterAll.IsChecked = true;
        FilterUnread.IsChecked = false;
        FilterStarred.IsChecked = false;
    }

    private void LoadMockFolder(string folder)
    {
        _currentFolder = folder;
        HighlightFolder(folder);
        ResetReadingPane();
        ClearSelection();
        ResetQuickFilter();
        ApplyCurrentFolderView();
        StatusText.Text = "Sign in to view your mailbox";
    }

    private List<InboxRow> GetFolderRows(string folder) => folder switch
    {
        // Starred is a view across folders, but deleted mail shouldn't resurface in it — Gmail
        // leaves trashed messages out of Starred for the same reason.
        "Starred" =>
        [
            .. _folderData.Where(pair => pair.Key != "Trash")
                          .SelectMany(pair => pair.Value)
                          .Where(r => r.Starred)
        ],
        _ => _folderData.TryGetValue(folder, out var rows) ? rows : [],
    };

    private IEnumerable<InboxRow> SortRows(IEnumerable<InboxRow> source)
    {
        var rows = source as IReadOnlyList<InboxRow> ?? [.. source];
        return _sortKey switch
        {
            "Sender" => rows.OrderBy(r => r.Sender, StringComparer.OrdinalIgnoreCase),
            "Subject" => rows.OrderBy(r => r.Subject, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderByDescending(r => r.Timestamp ?? DateTime.MinValue),
        };
    }

    private void ApplyCurrentFolderView()
    {
        if (!UseMockData)
        {
            ApplySortToLiveRows();
            return;
        }

        // Not signed in — nothing to show. No sample/placeholder mail; ListEmptyState's own text
        // (see UpdateListEmptyState) tells the user to sign in instead.
        _messages.Clear();
        UpdateListEmptyState();
    }

    /// <summary>
    /// Re-sorts what's on screen. Live mode only ever holds the current page, so this sorts that
    /// page rather than the whole folder.
    /// </summary>
    private void ApplySortToLiveRows()
    {
        var sorted = SortRows(_messages.ToList()).ToList();
        _messages.Clear();
        foreach (var row in sorted)
            _messages.Add(row);
        UpdateListEmptyState();
    }

    private void UpdateListEmptyState()
    {
        ListEmptyState.Text = UseMockData ? "Sign in to view your mailbox" : "No messages";
        ListEmptyState.Visibility = _messagesView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Was a dead no-op for real accounts — every call site (mark read/unread, delete, archive,
    /// bulk actions) called this expecting the sidebar badge to catch up, but it only ever did
    /// anything for sample data ("no sample mail to count" doesn't apply to a live mailbox that
    /// very much does have a real unread count). The badge only ever actually updated when
    /// something else happened to trigger a full folder refresh — switching away and back — which
    /// is exactly the "only updates when I leave and come back" symptom this fixes.
    /// </summary>
    private void UpdateInboxBadge()
    {
        if (UseMockData)
        {
            // No sample mail to count — sidebar badges just stay off until signed in.
            SetInboxBadge(0);
            SetDraftsBadge(0);
            return;
        }

        // Fire-and-forget: RefreshFoldersAsync catches its own exceptions and isn't gated by the
        // message-list's own in-flight guard, so this is safe to kick off without awaiting from
        // what are otherwise synchronous UI actions (a click, a row update).
        _ = RefreshFoldersAsync();
    }

    private void SetInboxBadge(int unread)
    {
        InboxUnreadCount.Text = unread.ToString();
        InboxUnreadBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        // The sidebar badge is invisible while the window is in the tray; the tray tooltip and
        // the taskbar overlay are the only places that count can still be read.
        _tray?.SetUnread(unread);
        TaskbarBadge.Apply(this, unread);
    }

    private void SetDraftsBadge(int count)
    {
        DraftsCount.Text = count.ToString();
        DraftsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Applies `updater` to the row with this id wherever it lives in `_folderData`, and mirrors
    /// the result into the currently visible `_messages` if it's shown there. Single source of
    /// truth per row, so a folder switch never "resets" a star/read change made earlier. In live
    /// mode `_folderData` is empty, so the visible page is the only thing to update.
    /// </summary>
    private InboxRow? UpdateRow(string id, Func<InboxRow, InboxRow> updater)
    {
        InboxRow? updated = null;
        foreach (var rows in _folderData.Values)
        {
            var idx = rows.FindIndex(r => r.Id == id);
            if (idx < 0)
                continue;
            updated = updater(rows[idx]);
            rows[idx] = updated;
        }

        for (var i = 0; i < _messages.Count; i++)
        {
            if (_messages[i].Id != id)
                continue;
            updated ??= updater(_messages[i]);
            _messages[i] = updated;
            break;
        }
        return updated;
    }

    private void RemoveMessageEverywhere(string id)
    {
        foreach (var rows in _folderData.Values)
            rows.RemoveAll(r => r.Id == id);
        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            if (_messages[i].Id == id)
                _messages.RemoveAt(i);
        }
    }

    /// <summary>
    /// Runs one live backend action with the error handling every call site needs: an expired
    /// session reads as a sign-in prompt rather than a generic failure.
    /// </summary>
    private async Task<bool> RunLiveAsync(Func<Task<bool>> action, string success, string failure)
    {
        try
        {
            if (await action())
            {
                StatusText.Text = success;
                return true;
            }
            StatusText.Text = failure;
            return false;
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
            return false;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{failure}: {ex.Message}";
            return false;
        }
    }


    // ---- Drag a message onto a sidebar folder to move it ----------------------------------------

    private System.Windows.Point? _dragStart;
    private const string MessageDragFormat = "PurplemailMessageIds";

    private void MessageRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = e.GetPosition(null);

    private void MessageRow_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
            return;
        if (sender is not FrameworkElement { Tag: InboxRow row })
            return;

        var pos = e.GetPosition(null);
        // A real drag threshold, not "any movement" — otherwise the tiny jitter between a click's
        // down and up starts a drag and eats the click that was meant to just open the message.
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragStart = null;

        // Dragging a row that's part of the current checkbox selection drags the whole selection,
        // the same "act on what's checked" scoping Move/Archive/Delete already use.
        var ids = _checkedIds.Contains(row.Id) ? _checkedIds.ToList() : [row.Id];
        var data = new System.Windows.DataObject(MessageDragFormat, ids);
        DragDrop.DoDragDrop((DependencyObject)sender, data, System.Windows.DragDropEffects.Move);
    }

    private void FolderDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(MessageDragFormat) || sender is not Border { Tag: string mailbox } border)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            return;
        }
        // Can't drop a message onto the folder it's already sitting in.
        if (string.Equals(mailbox, _currentFolder, StringComparison.OrdinalIgnoreCase))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Move;
        border.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE7, 0xD9, 0xF7));
    }

    // Clears the local Background it set — not to Transparent, but all the way back to "unset" —
    // so the FolderRow style's own hover trigger still governs it afterward, the same fix the
    // sidebar's active-folder highlighting needed for the same reason (see HighlightFolder).
    private void FolderDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is Border border)
            border.ClearValue(Border.BackgroundProperty);
    }

    private async void FolderDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is not Border { Tag: string mailbox } border)
            return;
        border.ClearValue(Border.BackgroundProperty);

        if (e.Data.GetData(MessageDragFormat) is not List<string> ids || ids.Count == 0)
            return;
        if (UseMockData)
        {
            foreach (var id in ids)
                MoveRowLocally(id, mailbox);
            ClearSelection();
            ResetReadingPane();
            ApplyCurrentFolderView();
            UpdateInboxBadge();
            StatusText.Text = $"Moved {ids.Count} to {FolderDisplayName(mailbox)} (sample data)";
            return;
        }

        // The fixed rows (Sent/Drafts/Archive/Trash) carry the friendly key as their Tag, not the
        // real IMAP mailbox path MoveToFolderAsync needs — the live-folder rows already carry the
        // real path, and SpecialMailbox passes that through unchanged (it only rewrites the fixed
        // keys it recognises), so this is safe to call either way.
        var targetMailbox = SpecialMailbox(mailbox);

        var moved = 0;
        ImapMailBackend.MoveUndo? undo = null;
        try
        {
            foreach (var id in ids)
            {
                if (!await _mail!.MoveToFolderAsync(id, targetMailbox))
                    continue;
                undo = ids.Count == 1 ? _mail.ConsumeLastMove() : null;
                RemoveMessageEverywhere(id);
                moved++;
            }
            StatusText.Text = moved == ids.Count
                ? $"Moved {moved} to {FolderDisplayName(mailbox)}"
                : $"Moved {moved} of {ids.Count} to {FolderDisplayName(mailbox)}";
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Move failed: {MailErrors.Friendly(ex)}";
        }

        if (undo is { } move)
        {
            var label = FolderDisplayName(mailbox);
            ShowActionUndo($"Moved to {label}", async () =>
            {
                if (await _mail!.UndoMoveAsync(move))
                    await RefreshMessagesAsync();
                else
                    StatusText.Text = "Couldn't undo — the message may have moved again since";
            });
        }

        ClearSelection();
        ResetReadingPane();
        await RefreshMessagesAsync();
    }

    private async void MessageRow_ContextMenu(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InboxRow row })
            return;

        MessageList.SelectedItem = row;

        var menu = new System.Windows.Controls.ContextMenu();

        void AddItem(string header, Action action)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        AddItem(row.Starred ? "Unstar" : "Star", () => _ = SetStarredAsync(row, !row.Starred));
        AddItem(row.Unread ? "Mark as read" : "Mark as unread", () => _ = SetRowUnreadAsync(row, !row.Unread));
        menu.Items.Add(new System.Windows.Controls.Separator());
        AddItem("Select", () =>
        {
            _checkedIds.Add(row.Id);
            UpdateBulkBar();
            _messagesView.Refresh();
        });
        AddItem("Archive", () => _ = ArchiveMessageAsync(row));
        AddItem("Delete", () => _ = RemoveMessageAsync(row, id => _mail!.DeleteAsync(id), "Deleted"));

        menu.Opened += (_, _) => menu.PopIn();
        menu.IsOpen = true;
        e.Handled = true;
        await Task.CompletedTask;
    }

    private async Task SetRowUnreadAsync(InboxRow row, bool unread)
    {
        if (!UseMockData && !await RunLiveAsync(
                () => _mail!.SetReadAsync(row.Id, !unread),
                unread ? "Marked as unread" : "Marked as read",
                unread ? "Couldn't mark as unread" : "Couldn't mark as read"))
            return;

        UpdateRow(row.Id, r => r with { Unread = unread });
        UpdateInboxBadge();
        _messagesView.Refresh();
        if (_openRow?.Id == row.Id && unread)
            ResetReadingPane();

        if (!UseMockData)
            await RefreshFoldersAsync();
    }


    // ---- Move to folder ------------------------------------------------------------------------

    /// <summary>
    /// Opens a menu of real destination folders. Applies to the checked rows when there are any,
    /// otherwise to the message that's open — the same scoping Gmail and Outlook use.
    /// </summary>
    private void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor)
            return;

        var targets = MoveTargets().ToList();
        if (targets.Count == 0)
        {
            StatusText.Text = "No other folders to move to";
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = anchor };
        // Same overflow risk as the account menu (see KeepInsideWindow's own comment): anchored
        // from a button that isn't near the window's own right edge, so the default left-aligned
        // Bottom placement can spill a long folder list past the window on a narrower layout.
        menu.KeepInsideWindow();
        foreach (var (mailbox, display) in targets)
        {
            var item = new System.Windows.Controls.MenuItem { Header = display, Tag = mailbox };
            item.Click += MoveTarget_Click;
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => menu.PopIn();
        menu.IsOpen = true;
    }

    private IEnumerable<(string Mailbox, string Display)> MoveTargets()
    {
        if (UseMockData)
        {
            return _folderData.Keys
                .Where(f => f != _currentFolder)
                .Select(f => (f, f));
        }

        return _liveFolders
            .Where(f => !string.Equals(f.Mailbox, _currentFolder, StringComparison.Ordinal))
            .Select(f => (f.Mailbox, new string(' ', f.Depth * 4) + f.Name));
    }

    private async void MoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string mailbox, Header: string display })
            return;

        var ids = _checkedIds.Count > 0
            ? _checkedIds.ToList()
            : _openRow is { } open ? [open.Id] : new List<string>();

        if (ids.Count == 0)
        {
            StatusText.Text = "Select a message to move";
            return;
        }

        var label = display.Trim();

        if (UseMockData)
        {
            foreach (var id in ids)
                MoveRowLocally(id, mailbox);
            ClearSelection();
            ResetReadingPane();
            ApplyCurrentFolderView();
            UpdateInboxBadge();
            StatusText.Text = $"Moved {ids.Count} to {label} (sample data)";
            return;
        }

        var moved = 0;
        // Only the single-message case gets an Undo offer — ConsumeLastMove only ever holds the
        // *most recent* move, so a bulk move would need to track one MoveUndo per message to undo
        // the whole batch; not worth the extra bookkeeping for how rarely "move" is used in bulk.
        Mail.ImapMailBackend.MoveUndo? undo = null;
        try
        {
            foreach (var id in ids)
            {
                if (!await _mail!.MoveToFolderAsync(id, mailbox))
                    continue;
                undo = ids.Count == 1 ? _mail.ConsumeLastMove() : null;
                RemoveMessageEverywhere(id);
                moved++;
            }
            StatusText.Text = moved == ids.Count ? $"Moved {moved} to {label}" : $"Moved {moved} of {ids.Count} to {label}";
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Move failed: {ex.Message}";
        }

        if (undo is { } move)
        {
            ShowActionUndo($"Moved to {label}", async () =>
            {
                if (await _mail!.UndoMoveAsync(move))
                    await RefreshMessagesAsync();
                else
                    StatusText.Text = "Couldn't undo — the message may have moved again since";
            });
        }

        ClearSelection();
        ResetReadingPane();
        await RefreshMessagesAsync();
    }

}
