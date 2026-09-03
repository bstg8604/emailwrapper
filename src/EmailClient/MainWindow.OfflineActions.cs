using EmailClient.Mail;

namespace EmailClient;

public partial class MainWindow
{
    /// <summary>
    /// Fired from SetConnectionState whenever the dot goes from Offline to anything that can
    /// actually reach the server. Replays queued actions in the order they were taken — a message
    /// deleted, then (offline) un-deleted by moving it back, must apply in that order or it ends up
    /// in the wrong place. Each replayed action is removed from the queue before it's attempted, not
    /// after, matching the same "don't double up on recovery paths" reasoning ScheduledSendStore's
    /// own replay uses: if a replay itself fails (message already gone, moved by another client
    /// meanwhile), that's a normal live-action failure now, not something offline mode should keep
    /// retrying forever.
    /// </summary>
    private async Task ReplayOfflineActionsAsync()
    {
        if (_offlineActions is null || UseMockData)
            return;

        var pending = _offlineActions.Load().OrderBy(a => a.CreatedUtc).ToList();
        if (pending.Count == 0)
            return;

        var replayed = 0;
        var skippedStale = 0;
        foreach (var action in pending)
        {
            _offlineActions.Remove(action.Id);

            // The queued id is an IMAP UID, only stable within one UIDVALIDITY generation — if the
            // folder got rebuilt while offline (rare, but possible), that same UID could now belong
            // to a completely different message. When a Message-ID was captured at queue time,
            // confirm it still matches before mutating anything; skip rather than guess if it
            // doesn't. Older queue entries (or ones queued before a message had ever been fetched)
            // carry no Message-ID to check against, so they fall back to trusting the UID alone,
            // same as before this existed.
            if (action.MessageIdHeader is { } expected)
            {
                var actual = await _mail!.GetMessageIdAsync(action.MessageId);
                if (!MessageIdsMatch(expected, actual))
                {
                    skippedStale++;
                    continue;
                }
            }

            try
            {
                _ = action.Kind switch
                {
                    OfflineActionKind.Delete => await _mail!.DeleteAsync(action.MessageId),
                    OfflineActionKind.Archive => await _mail!.ArchiveAsync(action.MessageId),
                    OfflineActionKind.MarkRead => await _mail!.SetReadAsync(action.MessageId, true),
                    OfflineActionKind.MarkUnread => await _mail!.SetReadAsync(action.MessageId, false),
                    OfflineActionKind.Move when action.TargetFolder is not null =>
                        await _mail!.MoveToFolderAsync(action.MessageId, action.TargetFolder),
                    _ => false,
                };
                replayed++;
            }
            catch (Exception)
            {
                // The message this pointed at may simply not exist anymore (already deleted from
                // another client while offline) — nothing further to do about that here.
            }
        }

        if (replayed > 0 || skippedStale > 0)
        {
            StatusText.Text = skippedStale == 0
                ? $"Synced {replayed} offline {(replayed == 1 ? "action" : "actions")}"
                : $"Synced {replayed} offline {(replayed == 1 ? "action" : "actions")} — {skippedStale} skipped (message changed while offline)";
            await RefreshFoldersAsync();
            await RefreshMessagesAsync();
        }
    }

    private static bool MessageIdsMatch(string a, string? b) =>
        b is not null && a.Trim().Trim('<', '>').Equals(b.Trim().Trim('<', '>'), StringComparison.OrdinalIgnoreCase);
}
