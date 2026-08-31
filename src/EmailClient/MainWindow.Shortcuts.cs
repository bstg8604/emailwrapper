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

/// <summary>Gmail-style keyboard shortcuts.</summary>

public partial class MainWindow
{
    // ---- Keyboard shortcuts (Gmail-style) ---------------------------------------------------

    private bool _gPending;
    private DispatcherTimer? _gPendingTimer;

    private async void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Never hijack typing — only Escape (to clear/blur the search box) is handled while a
        // text field has focus.
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                SearchBox.Text = "";
                Keyboard.ClearFocus();
            }
            return;
        }

        // '?' (Shift+/) opens Help (now part of the account page); plain '/' focuses search.
        if (e.Key == System.Windows.Input.Key.OemQuestion)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
                OpenAccountWindow(existing: _account, startPage: AccountPage.Help);
            else
                SearchBox.Focus();
            e.Handled = true;
            return;
        }

        // '#' (Shift+3) deletes, matching Gmail.
        if (e.Key == System.Windows.Input.Key.D3 && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
            if (_openRow is not null)
                await RemoveOpenMessageAsync(id => _mail!.DeleteAsync(id), "Deleted");
            else if (MessageList.SelectedItem is InboxRow row)
                await RemoveMessageAsync(row, id => _mail!.DeleteAsync(id), "Deleted");
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        if (_gPending)
        {
            _gPending = false;
            _gPendingTimer?.Stop();
            e.Handled = true;
            switch (e.Key)
            {
                case System.Windows.Input.Key.I: await GoToFolderAsync("Inbox"); break;
                case System.Windows.Input.Key.S: await GoToFolderAsync("Sent"); break;
                case System.Windows.Input.Key.D: await GoToFolderAsync("Drafts"); break;
                case System.Windows.Input.Key.A: await GoToFolderAsync("Archive"); break;
                case System.Windows.Input.Key.T: await GoToFolderAsync("Trash"); break;
            }
            return;
        }

        switch (e.Key)
        {
            case System.Windows.Input.Key.C:
                OpenCompose(bodyHtml: NewMessageBodyHtml);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.J:
                SelectAdjacentMessage(1);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.K:
                SelectAdjacentMessage(-1);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.U:
                ResetReadingPane();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.R:
                ReplyButton_Click(sender, e);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.A:
                ReplyAllButton_Click(sender, e);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.F:
                ForwardButton_Click(sender, e);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.E:
                e.Handled = true;
                if (_openRow is { } toArchive)
                    await ArchiveMessageAsync(toArchive);
                break;
            case System.Windows.Input.Key.G:
                _gPending = true;
                if (_gPendingTimer is null)
                {
                    _gPendingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                    _gPendingTimer.Tick += (_, _) => { _gPending = false; _gPendingTimer!.Stop(); };
                }
                _gPendingTimer.Start();
                e.Handled = true;
                break;
        }
    }

    private void SelectAdjacentMessage(int delta)
    {
        var items = _messagesView.Cast<InboxRow>().ToList();
        if (items.Count == 0)
            return;
        var currentIndex = _openRow is null ? -1 : items.FindIndex(r => r.Id == _openRow.Id);
        var nextIndex = Math.Clamp(currentIndex + delta, 0, items.Count - 1);
        MessageList.SelectedItem = items[nextIndex];
        MessageList.ScrollIntoView(items[nextIndex]);
    }

}
