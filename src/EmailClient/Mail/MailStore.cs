using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailClient.Automation;
using EmailClient.Diagnostics;
using Microsoft.Data.Sqlite;

namespace EmailClient.Mail;

/// <summary>
/// The local source of truth the UI reads from — a per-account SQLite database of message
/// metadata, bodies, folder unread counts and contacts. Unlike <see cref="MessageCache"/> (a
/// read-through display cache that's never authoritative — a cache miss just falls back to a live
/// IMAP call), every UI read goes through this and only this; a background <c>MailSyncEngine</c> is
/// what keeps it current from the server, on its own schedule, off the UI's critical path. This is
/// the same shape Apple Mail's own local envelope index gives it: folder switches, search and
/// message-list reads never wait on the network, because they were never asking it anything.
///
/// WAL journal mode with two genuinely separate SqliteConnection instances to the same file — one
/// dedicated to writes (the sync engine's batch upserts, and the UI's own immediate flag/delete
/// writes), one dedicated to reads (every UI read: folder switch, message list, search, message
/// open). This is what actually makes WAL's "readers don't block on a writer" guarantee real:
/// an earlier version of this store ran every read and write through the *same* connection object
/// behind one shared in-process lock, which fully serialized UI reads behind whatever the sync
/// engine happened to be writing at that moment — a background upsert of a couple hundred rows (or
/// a body backfill) could make a folder switch hang for its entire duration, even though the data
/// it wanted was sitting right there on disk. Two connections, two locks, fixes that: a write
/// transaction on _writeConn never blocks a concurrent read on _readConn.
/// </summary>
public sealed class MailStore : IDisposable
{
    /// <summary>Public — MainWindow's own pagination fallback logic (deciding whether a requested
    /// page is covered by what's actually synced) needs the same page size this store paginates
    /// GetMessages by, rather than a second, separately-maintained copy of the number.</summary>
    public const int PageSize = 50;

    private readonly SqliteConnection _writeConn;
    private readonly SqliteConnection _readConn;
    // Each connection object is still not safe for concurrent use from multiple threads at once
    // (its own internal command/transaction state isn't reentrant) — one lock per connection, so a
    // write and a read never contend with each other, only with another call of their own kind.
    private readonly Lock _writeGate = new();
    private readonly Lock _readGate = new();

    public MailStore(string accountEmail)
        : this(accountEmail, Path.Combine(Settings.AppDataPaths.Root, "cache"))
    {
    }

    /// <summary>Explicit cache root — lets tests work in a temp directory instead of the real
    /// profile, matching <see cref="MessageCache"/>'s own constructor shape.</summary>
    public MailStore(string accountEmail, string cacheRoot)
    {
        var dir = Path.Combine(cacheRoot, Key(accountEmail));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "mail.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        // Opened first: WAL mode and the schema itself only need setting/creating once, and doing
        // it here (before the read connection exists) means the read connection never observes a
        // half-migrated database.
        _writeConn = new SqliteConnection(connectionString);
        _writeConn.Open();
        Initialize();

        _readConn = new SqliteConnection(connectionString);
        _readConn.Open();
        using (var cmd = _readConn.CreateCommand())
        {
            // A brief SQLITE_BUSY is still possible for a read that lands in the narrow window a
            // WAL commit is flushing — retry internally instead of surfacing it as an error.
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Same filesystem-safe hashing as <see cref="MessageCache"/>'s own Key() — kept as a
    /// separate copy rather than a shared helper since the two are independent, unrelated stores
    /// (this one supersedes MessageCache's row/detail caching; nothing left references both), and
    /// duplicating six lines is cheaper than introducing a coupling between them for it.</summary>
    private static string Key(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 10);
    }

    private void Initialize()
    {
        using var cmd = _writeConn.CreateCommand();
        // WAL, not the default rollback-journal mode: lets the read connection keep working against
        // a consistent snapshot while this (write) connection has a transaction in flight, instead
        // of reads blocking on the writer.
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS messages (
              mailbox TEXT NOT NULL, uid INTEGER NOT NULL, message_id TEXT,
              subject TEXT NOT NULL, sender_name TEXT NOT NULL, sender_address TEXT NOT NULL,
              snippet TEXT NOT NULL, date_utc TEXT NOT NULL,
              unread INTEGER NOT NULL, starred INTEGER NOT NULL, has_attachment INTEGER NOT NULL,
              PRIMARY KEY (mailbox, uid)
            );
            CREATE INDEX IF NOT EXISTS idx_messages_mailbox_date ON messages(mailbox, date_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_messages_message_id ON messages(message_id);

            -- A message's References header, one row per referenced ancestor id, normalized rather
            -- than a JSON blob column — this is what makes "find replies to the opened message"
            -- (walking this table backwards: who references *my* message_id) a fast indexed lookup
            -- instead of a full scan through every message's JSON. Populated from two sources:
            -- MailSyncEngine's own envelope sync (MessageSummaryItems.References) for a message
            -- that's merely been listed, and SaveBody's own References for one that's been opened —
            -- either is enough, so coverage isn't gated on a message having been opened.
            CREATE TABLE IF NOT EXISTS message_references (
              mailbox TEXT NOT NULL, uid INTEGER NOT NULL, reference_id TEXT NOT NULL,
              PRIMARY KEY (mailbox, uid, reference_id)
            );
            CREATE INDEX IF NOT EXISTS idx_message_references_reference_id ON message_references(reference_id);

            CREATE TABLE IF NOT EXISTS message_bodies (
              mailbox TEXT NOT NULL, uid INTEGER NOT NULL,
              body_html TEXT NOT NULL, body_text TEXT NOT NULL,
              to_line TEXT NOT NULL, cc_line TEXT NOT NULL, bcc_line TEXT NOT NULL,
              subject TEXT NOT NULL, from_line TEXT NOT NULL, date_display TEXT NOT NULL, date_utc TEXT,
              message_id TEXT, attachments_json TEXT, calendar_json TEXT, references_json TEXT,
              unsubscribe_url TEXT, unsubscribe_mailto TEXT, fetched_at_utc TEXT NOT NULL,
              PRIMARY KEY (mailbox, uid)
            );

            CREATE TABLE IF NOT EXISTS folders (
              mailbox TEXT PRIMARY KEY, display_name TEXT NOT NULL,
              unread_count INTEGER NOT NULL, depth INTEGER NOT NULL, total_count INTEGER NOT NULL DEFAULT 0,
              last_synced_utc TEXT
            );

            CREATE TABLE IF NOT EXISTS contacts (
              address TEXT PRIMARY KEY, display_name TEXT NOT NULL, score INTEGER NOT NULL, last_seen_utc TEXT
            );

            -- External-content FTS5 table over `messages`: no separate copy of the text, just an
            -- index kept in sync by the triggers below.
            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
              subject, sender_name, sender_address,
              content='messages', content_rowid='rowid'
            );
            CREATE TRIGGER IF NOT EXISTS messages_ai AFTER INSERT ON messages BEGIN
              INSERT INTO messages_fts(rowid, subject, sender_name, sender_address)
              VALUES (new.rowid, new.subject, new.sender_name, new.sender_address);
            END;
            CREATE TRIGGER IF NOT EXISTS messages_ad AFTER DELETE ON messages BEGIN
              INSERT INTO messages_fts(messages_fts, rowid, subject, sender_name, sender_address)
              VALUES ('delete', old.rowid, old.subject, old.sender_name, old.sender_address);
            END;
            CREATE TRIGGER IF NOT EXISTS messages_au AFTER UPDATE ON messages BEGIN
              INSERT INTO messages_fts(messages_fts, rowid, subject, sender_name, sender_address)
              VALUES ('delete', old.rowid, old.subject, old.sender_name, old.sender_address);
              INSERT INTO messages_fts(rowid, subject, sender_name, sender_address)
              VALUES (new.rowid, new.subject, new.sender_name, new.sender_address);
            END;

            -- Body text search — only ever populated for a message that's actually been fetched in
            -- full (opened by the user, or reached by the throttled backfill in MailSyncEngine), so
            -- coverage grows over time rather than requiring every message's body up front.
            -- body_text is plain text (MailText.HtmlToPlainText), not raw HTML: smaller to index and
            -- markup tags would otherwise pollute match relevance and highlighting.
            CREATE VIRTUAL TABLE IF NOT EXISTS body_fts USING fts5(
              body_text, content='message_bodies', content_rowid='rowid'
            );
            CREATE TRIGGER IF NOT EXISTS bodies_ai AFTER INSERT ON message_bodies BEGIN
              INSERT INTO body_fts(rowid, body_text) VALUES (new.rowid, new.body_text);
            END;
            CREATE TRIGGER IF NOT EXISTS bodies_ad AFTER DELETE ON message_bodies BEGIN
              INSERT INTO body_fts(body_fts, rowid, body_text) VALUES ('delete', old.rowid, old.body_text);
            END;
            CREATE TRIGGER IF NOT EXISTS bodies_au AFTER UPDATE ON message_bodies BEGIN
              INSERT INTO body_fts(body_fts, rowid, body_text) VALUES ('delete', old.rowid, old.body_text);
              INSERT INTO body_fts(rowid, body_text) VALUES (new.rowid, new.body_text);
            END;
            """;
        cmd.ExecuteNonQuery();
        Migrate(cmd);
    }

    /// <summary>Schema changes for a database that already exists from an earlier version — "CREATE
    /// TABLE IF NOT EXISTS" above only helps a brand-new database; an existing `folders` table
    /// (this app's very first release already wrote one) keeps its original columns forever unless
    /// something explicitly adds to it. SQLite has no "ADD COLUMN IF NOT EXISTS", so this just tries
    /// the ALTER and swallows the failure a column that's already there raises.</summary>
    private static void Migrate(SqliteCommand cmd)
    {
        try
        {
            cmd.CommandText = "ALTER TABLE folders ADD COLUMN total_count INTEGER NOT NULL DEFAULT 0;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Already has the column (every database created by this version's own CREATE TABLE
            // above does) — nothing to do.
        }
    }

    // ---- Messages -----------------------------------------------------------------------------

    /// <summary>Cheap indexed existence check — used by MainWindow's conversation-gathering code to
    /// tell which of an opened message's own ancestor References it can already answer locally
    /// versus which ones fall outside the synced window and need a targeted live IMAP lookup as a
    /// fallback (see GatherConversationAsync's own remarks).</summary>
    public bool HasMessageId(string messageId)
    {
        lock (_readGate)
        {
            using var cmd = _readConn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM messages WHERE message_id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", messageId);
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>Page 1 = newest. Matches ImapMailBackend.ListMessagesAsync's own paging convention
    /// so MessagePage's shape (and every caller's expectations) carries over unchanged.</summary>
    public MessagePage GetMessages(string mailbox, int page)
    {
        lock (_readGate)
        {
            var total = ScalarInt("SELECT COUNT(*) FROM messages WHERE mailbox = $mailbox;",
                ("$mailbox", mailbox));
            var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
            page = Math.Clamp(page, 1, pageCount);
            if (total == 0)
                return new MessagePage([], page, pageCount, 0, mailbox);

            using var cmd = _readConn.CreateCommand();
            cmd.CommandText = """
                SELECT uid, message_id, subject, sender_name, sender_address, snippet, date_utc,
                       unread, starred, has_attachment
                FROM messages WHERE mailbox = $mailbox
                ORDER BY date_utc DESC
                LIMIT $limit OFFSET $offset;
                """;
            cmd.Parameters.AddWithValue("$mailbox", mailbox);
            cmd.Parameters.AddWithValue("$limit", PageSize);
            cmd.Parameters.AddWithValue("$offset", (page - 1) * PageSize);

            var rows = new List<InboxRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(ReadRow(reader, mailbox: null)); // same-folder read: Mailbox stays null, existing convention

            return new MessagePage(rows, page, pageCount, total, mailbox);
        }
    }

    /// <summary>Batch upsert inside one transaction — called by the sync engine after fetching a
    /// folder's envelopes, and directly by an action (mark read/starred) that already knows exactly
    /// what changed rather than waiting for the next sync sweep to notice.</summary>
    /// <param name="messageIds">Message-ID header per row, keyed by <see cref="InboxRow.Id"/> (the
    /// IMAP UID as a string) — not carried on InboxRow itself (a JSON-serialized automation shape
    /// used elsewhere, not worth widening just for this), so the sync engine (which fetches
    /// envelopes directly and has the header right there) passes it in separately. Optional: a
    /// caller updating only flags for already-synced rows (mark read/starred) can omit this and the
    /// existing message_id column value is left untouched.</param>
    /// <param name="references">This row's own References header (its ancestor chain), keyed the
    /// same way as <paramref name="messageIds"/> — feeds <c>message_references</c>, which is what
    /// lets FindConversationSiblings find *replies* to a message, not just what it replied to.
    /// Omitted entirely by a flags-only update, same as messageIds; a row present in
    /// <paramref name="messageIds"/> but absent here (or with a null/empty list) simply isn't known
    /// to reference anything yet, not that it's known to reference nothing — its existing
    /// message_references rows, if any, are left alone rather than cleared.</param>
    public void UpsertMessages(
        string mailbox, IReadOnlyList<InboxRow> rows, IReadOnlyDictionary<string, string?>? messageIds = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>?>? references = null)
    {
        if (rows.Count == 0)
            return;
        lock (_writeGate)
        {
            using var tx = _writeConn.BeginTransaction();
            using var cmd = _writeConn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO messages (mailbox, uid, message_id, subject, sender_name, sender_address,
                                       snippet, date_utc, unread, starred, has_attachment)
                VALUES ($mailbox, $uid, $message_id, $subject, $sender_name, $sender_address,
                        $snippet, $date_utc, $unread, $starred, $has_attachment)
                ON CONFLICT(mailbox, uid) DO UPDATE SET
                  -- COALESCE, not a straight overwrite: a flags-only update (mark read/starred)
                  -- passes no message id at all, and must not wipe out one a previous sync already
                  -- recorded — FindConversationSiblings depends on this column staying populated.
                  message_id=COALESCE(excluded.message_id, messages.message_id), subject=excluded.subject,
                  sender_name=excluded.sender_name, sender_address=excluded.sender_address,
                  snippet=excluded.snippet, date_utc=excluded.date_utc,
                  unread=excluded.unread, starred=excluded.starred,
                  has_attachment=excluded.has_attachment;
                """;
            var pMailbox = cmd.Parameters.Add("$mailbox", SqliteType.Text);
            var pUid = cmd.Parameters.Add("$uid", SqliteType.Integer);
            var pMessageId = cmd.Parameters.Add("$message_id", SqliteType.Text);
            var pSubject = cmd.Parameters.Add("$subject", SqliteType.Text);
            var pSenderName = cmd.Parameters.Add("$sender_name", SqliteType.Text);
            var pSenderAddress = cmd.Parameters.Add("$sender_address", SqliteType.Text);
            var pSnippet = cmd.Parameters.Add("$snippet", SqliteType.Text);
            var pDateUtc = cmd.Parameters.Add("$date_utc", SqliteType.Text);
            var pUnread = cmd.Parameters.Add("$unread", SqliteType.Integer);
            var pStarred = cmd.Parameters.Add("$starred", SqliteType.Integer);
            var pHasAttachment = cmd.Parameters.Add("$has_attachment", SqliteType.Integer);

            using var deleteRefs = _writeConn.CreateCommand();
            deleteRefs.Transaction = tx;
            deleteRefs.CommandText = "DELETE FROM message_references WHERE mailbox = $mailbox AND uid = $uid;";
            var pDelMailbox = deleteRefs.Parameters.Add("$mailbox", SqliteType.Text);
            var pDelUid = deleteRefs.Parameters.Add("$uid", SqliteType.Integer);

            using var insertRef = _writeConn.CreateCommand();
            insertRef.Transaction = tx;
            insertRef.CommandText = """
                INSERT OR IGNORE INTO message_references (mailbox, uid, reference_id)
                VALUES ($mailbox, $uid, $reference_id);
                """;
            var pRefMailbox = insertRef.Parameters.Add("$mailbox", SqliteType.Text);
            var pRefUid = insertRef.Parameters.Add("$uid", SqliteType.Integer);
            var pRefId = insertRef.Parameters.Add("$reference_id", SqliteType.Text);

            foreach (var row in rows)
            {
                pMailbox.Value = mailbox;
                pUid.Value = row.Id;
                pMessageId.Value = messageIds is not null && messageIds.TryGetValue(row.Id, out var mid) && mid is not null
                    ? mid
                    : DBNull.Value;
                pSubject.Value = row.Subject;
                pSenderName.Value = row.Sender;
                pSenderAddress.Value = row.SenderAddress;
                pSnippet.Value = row.Snippet;
                pDateUtc.Value = (row.Timestamp ?? DateTime.UtcNow).ToUniversalTime().ToString("O");
                pUnread.Value = row.Unread ? 1 : 0;
                pStarred.Value = row.Starred ? 1 : 0;
                pHasAttachment.Value = row.HasAttachment ? 1 : 0;
                cmd.ExecuteNonQuery();

                if (references is not null && references.TryGetValue(row.Id, out var refs) && refs is { Count: > 0 })
                {
                    pDelMailbox.Value = mailbox;
                    pDelUid.Value = row.Id;
                    deleteRefs.ExecuteNonQuery();

                    pRefMailbox.Value = mailbox;
                    pRefUid.Value = row.Id;
                    foreach (var refId in refs)
                    {
                        if (string.IsNullOrWhiteSpace(refId))
                            continue;
                        pRefId.Value = refId;
                        insertRef.ExecuteNonQuery();
                    }
                }
            }
            tx.Commit();
        }
    }

    /// <summary>Directly updates one message's flags without a full re-sync — used by the same call
    /// site that already tells ImapMailBackend to mark read/starred, so the local store reflects the
    /// action immediately instead of waiting for the next background sweep.</summary>
    public void UpdateFlags(string mailbox, string uid, bool? unread = null, bool? starred = null)
    {
        if (unread is null && starred is null)
            return;
        lock (_writeGate)
        {
            using var cmd = _writeConn.CreateCommand();
            var sets = new List<string>();
            if (unread is { } u) { sets.Add("unread = $unread"); cmd.Parameters.AddWithValue("$unread", u ? 1 : 0); }
            if (starred is { } s) { sets.Add("starred = $starred"); cmd.Parameters.AddWithValue("$starred", s ? 1 : 0); }
            cmd.CommandText = $"UPDATE messages SET {string.Join(", ", sets)} WHERE mailbox = $mailbox AND uid = $uid;";
            cmd.Parameters.AddWithValue("$mailbox", mailbox);
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Removes one message — used when a delete/archive/move takes it out of the folder
    /// the local store still has it filed under, so the list doesn't keep showing a message that's
    /// already gone until the next full sync sweep happens to notice.</summary>
    public void DeleteMessage(string mailbox, string uid)
    {
        lock (_writeGate)
        {
            using var tx = _writeConn.BeginTransaction();
            using var cmd = _writeConn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM messages WHERE mailbox = $mailbox AND uid = $uid;";
            cmd.Parameters.AddWithValue("$mailbox", mailbox);
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.ExecuteNonQuery();

            // No foreign key cascade configured for this table (kept simple, matching every other
            // table here) — an explicit delete keeps FindConversationSiblings from ever surfacing a
            // reference row that points at a message which no longer exists.
            using var deleteRefs = _writeConn.CreateCommand();
            deleteRefs.Transaction = tx;
            deleteRefs.CommandText = "DELETE FROM message_references WHERE mailbox = $mailbox AND uid = $uid;";
            deleteRefs.Parameters.AddWithValue("$mailbox", mailbox);
            deleteRefs.Parameters.AddWithValue("$uid", uid);
            deleteRefs.ExecuteNonQuery();

            tx.Commit();
        }
    }

    /// <summary>Candidates for MailSyncEngine's throttled body backfill — synced messages in
    /// <paramref name="mailbox"/> that don't have a body cached yet, newest first (a recent message
    /// is more likely to matter to a search than one from years ago, so the backfill works forward
    /// from "most likely useful" rather than a fixed/arbitrary order).</summary>
    public IReadOnlyList<string> GetUidsMissingBody(string mailbox, int limit)
    {
        lock (_readGate)
        {
            using var cmd = _readConn.CreateCommand();
            cmd.CommandText = """
                SELECT m.uid FROM messages m
                WHERE m.mailbox = $mailbox
                  AND NOT EXISTS (SELECT 1 FROM message_bodies b WHERE b.mailbox = m.mailbox AND b.uid = m.uid)
                ORDER BY m.date_utc DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$mailbox", mailbox);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var uids = new List<string>();
            while (reader.Read())
                uids.Add(reader.GetInt64(0).ToString());
            return uids;
        }
    }

    /// <summary>How many synced messages in <paramref name="mailbox"/> still have no body cached —
    /// used only to show real backfill progress ("140 of 340 indexed"), not to decide what to fetch
    /// (see <see cref="GetUidsMissingBody"/> for that).</summary>
    public int CountUidsMissingBody(string mailbox)
    {
        lock (_readGate)
        {
            using var cmd = _readConn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) FROM messages m
                WHERE m.mailbox = $mailbox
                  AND NOT EXISTS (SELECT 1 FROM message_bodies b WHERE b.mailbox = m.mailbox AND b.uid = m.uid);
                """;
            cmd.Parameters.AddWithValue("$mailbox", mailbox);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // ---- Bodies ---------------------------------------------------------------------------------

    /// <summary>Keyed by (mailbox, uid) — the same composite key `messages` itself uses, not a bare
    /// UID string: a UID is only unique within its own folder (Inbox UID 42 and Sent UID 42 are
    /// unrelated messages), so a bare-UID key risked one folder's cached body answering for
    /// another's.</summary>
    public MessageDetail? GetBody(string mailbox, string uid)
    {
        lock (_readGate)
        {
            using var cmd = _readConn.CreateCommand();
            cmd.CommandText = """
                SELECT body_html, to_line, cc_line, bcc_line, subject, from_line, date_display,
                       message_id, attachments_json, calendar_json, references_json,
                       unsubscribe_url, unsubscribe_mailto, date_utc
                FROM message_bodies WHERE mailbox = $mailbox AND uid = $uid;
                """;
            cmd.Parameters.AddWithValue("$mailbox", mailbox);
            cmd.Parameters.AddWithValue("$uid", uid);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            return new MessageDetail(
                Subject: reader.GetString(4),
                From: reader.GetString(5),
                Date: reader.GetString(6),
                BodyHtml: reader.GetString(0),
                To: reader.GetString(1),
                Cc: reader.GetString(2),
                Attachments: DeserializeOrNull<List<MailAttachment>>(reader, 8),
                Bcc: reader.GetString(3),
                Calendar: DeserializeOrNull<CalendarInvite>(reader, 9),
                Timestamp: reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13)).ToLocalTime(),
                MessageId: reader.IsDBNull(7) ? null : reader.GetString(7),
                References: DeserializeOrNull<List<string>>(reader, 10),
                UnsubscribeUrl: reader.IsDBNull(11) ? null : reader.GetString(11),
                UnsubscribeMailto: reader.IsDBNull(12) ? null : reader.GetString(12));
        }
    }

    public void SaveBody(string mailbox, string uid, MessageDetail detail)
    {
        lock (_writeGate)
        {
            using var cmd = _writeConn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO message_bodies (mailbox, uid, body_html, body_text, to_line, cc_line, bcc_line, subject,
                    from_line, date_display, date_utc, message_id, attachments_json, calendar_json,
                    references_json, unsubscribe_url, unsubscribe_mailto, fetched_at_utc)
                VALUES ($mailbox, $uid, $body, $body_text, $to, $cc, $bcc, $subject, $from, $date, $date_utc, $message_id,
                        $attachments, $calendar, $references, $unsub_url, $unsub_mailto, $fetched)
                ON CONFLICT(mailbox, uid) DO UPDATE SET
                  body_html=excluded.body_html, body_text=excluded.body_text,
                  to_line=excluded.to_line, cc_line=excluded.cc_line,
                  bcc_line=excluded.bcc_line, subject=excluded.subject, from_line=excluded.from_line,
                  date_display=excluded.date_display, date_utc=excluded.date_utc,
                  message_id=excluded.message_id, attachments_json=excluded.attachments_json,
                  calendar_json=excluded.calendar_json, references_json=excluded.references_json,
                  unsubscribe_url=excluded.unsubscribe_url, unsubscribe_mailto=excluded.unsubscribe_mailto,
                  fetched_at_utc=excluded.fetched_at_utc;
                """;
            cmd.Parameters.AddWithValue("$mailbox", mailbox);
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$body", detail.BodyHtml);
            cmd.Parameters.AddWithValue("$body_text", MailText.HtmlToPlainText(detail.BodyHtml));
            cmd.Parameters.AddWithValue("$to", detail.To);
            cmd.Parameters.AddWithValue("$cc", detail.Cc);
            cmd.Parameters.AddWithValue("$bcc", detail.Bcc);
            cmd.Parameters.AddWithValue("$subject", detail.Subject);
            cmd.Parameters.AddWithValue("$from", detail.From);
            cmd.Parameters.AddWithValue("$date", detail.Date);
            cmd.Parameters.AddWithValue("$date_utc", (object?)detail.Timestamp?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$message_id", (object?)detail.MessageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$attachments", (object?)Serialize(detail.Attachments) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$calendar", (object?)Serialize(detail.Calendar) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$references", (object?)Serialize(detail.References) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$unsub_url", (object?)detail.UnsubscribeUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$unsub_mailto", (object?)detail.UnsubscribeMailto ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fetched", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();

            // Two things a full body fetch can teach FindConversationSiblings that a lightweight
            // envelope sync might not have yet, for a message reached via some other path (search,
            // a sibling fetch, cold-start fallback) than MailSyncEngine's own sweep:
            if (!string.IsNullOrWhiteSpace(detail.MessageId))
            {
                // (1) this message's own message_id, if the `messages` row exists but wasn't
                // synced with one yet (COALESCE — never overwrite a value already known).
                using var updateMessageId = _writeConn.CreateCommand();
                updateMessageId.CommandText = """
                    UPDATE messages SET message_id = COALESCE(message_id, $message_id)
                    WHERE mailbox = $mailbox AND uid = $uid;
                    """;
                updateMessageId.Parameters.AddWithValue("$message_id", detail.MessageId);
                updateMessageId.Parameters.AddWithValue("$mailbox", mailbox);
                updateMessageId.Parameters.AddWithValue("$uid", uid);
                updateMessageId.ExecuteNonQuery();
            }
            if (detail.References is { Count: > 0 })
            {
                // (2) its References chain — the ancestor half of threading; see
                // message_references' own remarks for why this matters for the *reply* direction.
                using var deleteRefs = _writeConn.CreateCommand();
                deleteRefs.CommandText = "DELETE FROM message_references WHERE mailbox = $mailbox AND uid = $uid;";
                deleteRefs.Parameters.AddWithValue("$mailbox", mailbox);
                deleteRefs.Parameters.AddWithValue("$uid", uid);
                deleteRefs.ExecuteNonQuery();

                using var insertRef = _writeConn.CreateCommand();
                insertRef.CommandText = """
                    INSERT OR IGNORE INTO message_references (mailbox, uid, reference_id)
                    VALUES ($mailbox, $uid, $reference_id);
                    """;
                insertRef.Parameters.AddWithValue("$mailbox", mailbox);
                insertRef.Parameters.AddWithValue("$uid", uid);
                var pRefId = insertRef.Parameters.Add("$reference_id", SqliteType.Text);
                foreach (var refId in detail.References)
                {
                    if (string.IsNullOrWhiteSpace(refId))
                        continue;
                    pRefId.Value = refId;
                    insertRef.ExecuteNonQuery();
                }
            }
        }
    }

    // ---- Folders --------------------------------------------------------------------------------

    public IReadOnlyList<MailFolder> GetFolders()
    {
        lock (_readGate)
        {
            using var cmd = _readConn.CreateCommand();
            cmd.CommandText = "SELECT mailbox, display_name, unread_count, depth, total_count FROM folders ORDER BY depth, display_name;";
            using var reader = cmd.ExecuteReader();
            var result = new List<MailFolder>();
            while (reader.Read())
                result.Add(new MailFolder(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));
            return result;
        }
    }

    /// <summary>The folder's real, full message count (0 if the folder's never been seen by
    /// UpsertFolder yet) — see MailFolder.Total's own remarks on why the message-list pager needs
    /// this rather than trusting however many rows happen to be synced.</summary>
    public int GetFolderTotal(string mailbox)
    {
        lock (_readGate)
        {
            using var cmd = _readConn.CreateCommand();
            cmd.CommandText = "SELECT total_count FROM folders WHERE mailbox = $mailbox;";
            cmd.Parameters.AddWithValue("$mailbox", mailbox);
            return cmd.ExecuteScalar() is long total ? (int)total : 0;
        }
    }

    /// <summary>Adjusts one folder's cached unread_count by <paramref name="delta"/> (clamped at 0)
    /// — used wherever the app already knows a single message's read state just changed locally
    /// (mark read/unread, opening an unread message) so the sidebar/badge reflects it immediately,
    /// instead of the next full folder resync's real IMAP-derived count. Without this, a fire-and-
    /// forget RefreshFoldersAsync (kicked off right after the same click, to eventually reconcile
    /// with the server) reads this same table's still-stale count first and stomps whatever
    /// optimistic UI update the caller just applied — a live report: the badge would tick down and
    /// immediately jump back up.</summary>
    public void AdjustFolderUnread(string mailbox, int delta)
    {
        lock (_writeGate)
        {
            using var cmd = _writeConn.CreateCommand();
            cmd.CommandText = """
                UPDATE folders SET unread_count = MAX(0, unread_count + $delta) WHERE mailbox = $mailbox;
                """;
            cmd.Parameters.AddWithValue("$delta", delta);
            cmd.Parameters.AddWithValue("$mailbox", mailbox);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpsertFolder(MailFolder folder)
    {
        lock (_writeGate)
        {
            using var cmd = _writeConn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO folders (mailbox, display_name, unread_count, depth, total_count, last_synced_utc)
                VALUES ($mailbox, $name, $unread, $depth, $total, $synced)
                ON CONFLICT(mailbox) DO UPDATE SET
                  display_name=excluded.display_name, unread_count=excluded.unread_count,
                  depth=excluded.depth, total_count=excluded.total_count, last_synced_utc=excluded.last_synced_utc;
                """;
            cmd.Parameters.AddWithValue("$mailbox", folder.Mailbox);
            cmd.Parameters.AddWithValue("$name", folder.Name);
            cmd.Parameters.AddWithValue("$unread", folder.Unread);
            cmd.Parameters.AddWithValue("$depth", folder.Depth);
            cmd.Parameters.AddWithValue("$total", folder.Total);
            cmd.Parameters.AddWithValue("$synced", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    // ---- Search ---------------------------------------------------------------------------------

    /// <summary>Instant local search over subject/sender/recipient — not body text (see MailStore's
    /// own class remarks). FTS5's own query syntax (AND/OR/prefix*) would surprise a user typing a
    /// plain phrase the way Ctrl+F expects, so the raw query is treated as one literal phrase by
    /// quoting it, not passed through as an FTS5 query expression.</summary>
    /// <param name="mailbox">Null searches every synced folder; set to scope to one folder (the
    /// UI's own "search this folder" behavior) — matching rows from that folder come back with
    /// Mailbox null (the usual "whatever folder is currently open" convention), same as
    /// GetMessages; this parameter only matters for output when null (every result then needs its
    /// own mailbox tagged, same reasoning as FindConversationSiblings).</param>
    public IReadOnlyList<InboxRow> Search(string query, string? mailbox = null, int maxResults = 300)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        lock (_readGate)
        {
            using var cmd = _readConn.CreateCommand();
            // Union of two match sources: subject/sender/recipient (messages_fts, covers every
            // synced message regardless of whether its body's ever been fetched) and body text
            // (body_fts, only covers messages that have actually been opened or reached by the
            // throttled backfill — see message_bodies' own remarks). A message needs only one hit
            // from either to show up; DISTINCT because a message could match both.
            cmd.CommandText = $"""
                SELECT DISTINCT m.mailbox, m.uid, m.message_id, m.subject, m.sender_name, m.sender_address,
                       m.snippet, m.date_utc, m.unread, m.starred, m.has_attachment
                FROM messages m
                WHERE (
                    m.rowid IN (SELECT rowid FROM messages_fts WHERE messages_fts MATCH $query)
                    OR EXISTS (
                        SELECT 1 FROM message_bodies b
                        WHERE b.mailbox = m.mailbox AND b.uid = m.uid
                          AND b.rowid IN (SELECT rowid FROM body_fts WHERE body_fts MATCH $query)
                    )
                )
                {(mailbox is not null ? "AND m.mailbox = $mailbox" : "")}
                ORDER BY m.date_utc DESC
                LIMIT $limit;
                """;
            // A bare phrase in double quotes is FTS5's own literal-phrase syntax — this still lets
            // "*" work as a trailing prefix wildcard if a user types one, while not choking on
            // punctuation FTS5's query parser would otherwise reject as invalid syntax.
            cmd.Parameters.AddWithValue("$query", "\"" + query.Replace("\"", "\"\"") + "\"");
            cmd.Parameters.AddWithValue("$limit", maxResults);
            if (mailbox is not null)
                cmd.Parameters.AddWithValue("$mailbox", mailbox);

            var rows = new List<InboxRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var rowMailbox = reader.GetString(0);
                rows.Add(ReadRowFrom(reader, offset: 1, mailbox is not null && rowMailbox == mailbox ? null : rowMailbox));
            }
            return rows;
        }
    }

    // ---- Reply status ---------------------------------------------------------------------------

    /// <summary>Attaches Roundcube/IITB-webmail-style reply-status flags to an already-fetched page
    /// of rows — one JOIN query for the whole page, not one per row. A folder's row shows Replied
    /// when a message_references shows the Sent folder already referencing it (an Inbox row: "you
    /// replied to this"); the Sent folder's own rows show HasReply instead, when some *other*
    /// folder's message references it back ("this got a reply"). Only ever known for a row whose
    /// own message_id is already synced locally — a row reached via the live-IMAP-fallback path
    /// (a page beyond MailSyncEngine's synced window) simply keeps both flags false, the same
    /// "may not know yet" gap FindConversationSiblings' own ancestor lookup already accepts rather
    /// than paying a live round trip just to answer this.</summary>
    public IReadOnlyList<InboxRow> AttachReplyStatus(IReadOnlyList<InboxRow> rows, string mailbox, string sentMailbox)
    {
        if (rows.Count == 0)
            return rows;

        var isSentFolder = string.Equals(mailbox, sentMailbox, StringComparison.OrdinalIgnoreCase);
        lock (_readGate)
        {
            using var cmd = _readConn.CreateCommand();
            var uidPlaceholders = string.Join(",", rows.Select((_, i) => $"$uid{i}"));
            cmd.CommandText = $"""
                SELECT m.uid,
                       EXISTS (SELECT 1 FROM message_references r
                               WHERE r.reference_id = m.message_id AND r.mailbox = $sentMailbox) AS replied,
                       EXISTS (SELECT 1 FROM message_references r
                               WHERE r.reference_id = m.message_id AND r.mailbox <> $sentMailbox) AS has_reply
                FROM messages m
                WHERE m.mailbox = $mailbox AND m.uid IN ({uidPlaceholders}) AND m.message_id IS NOT NULL;
                """;
            cmd.Parameters.AddWithValue("$mailbox", mailbox);
            cmd.Parameters.AddWithValue("$sentMailbox", sentMailbox);
            for (var i = 0; i < rows.Count; i++)
                cmd.Parameters.AddWithValue($"$uid{i}", rows[i].Id);

            var status = new Dictionary<string, (bool Replied, bool HasReply)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                status[reader.GetInt64(0).ToString()] = (reader.GetInt32(1) != 0, reader.GetInt32(2) != 0);

            if (status.Count == 0)
                return rows;

            return rows.Select(r => status.TryGetValue(r.Id, out var s)
                ? r with { Replied = !isSentFolder && s.Replied, HasReply = isSentFolder && s.HasReply }
                : r).ToList();
        }
    }

    // ---- Conversation siblings --------------------------------------------------------------------

    /// <summary>Local equivalent of ImapMailBackend.FindConversationSiblingsAsync — a query across
    /// every synced folder instead of a live multi-folder IMAP SEARCH. This is what actually removes
    /// the lock-contention this app hit repeatedly: there's no IMAP round trip here at all.
    ///
    /// Ancestors only — messages the opened one replied to (their own message_id is one of the
    /// opened message's References) — deliberately never the reverse (messages that reply *to* the
    /// opened one, via message_references). An earlier version of this method matched both
    /// directions on the reasoning that a reply should show the full thread; a live report corrected
    /// that: opening an *earlier* message in a thread must show only what it itself replied to, never
    /// a later reply that didn't exist yet from that message's own point of view. Opening the later
    /// reply still shows the earlier message below it — that's the ancestor direction, unaffected.
    ///
    /// <paramref name="max"/> defaults to a generous ceiling, not a real limit — a hardcoded cap of
    /// 8 here used to silently drop a real thread's older messages once it grew past that, with no
    /// way to see them at all. This is a local, indexed lookup (no network cost per row), so the
    /// only reason for any ceiling at all is guarding against a pathological reference chain, not
    /// realistic thread length.
    /// </summary>
    public IReadOnlyList<InboxRow> FindConversationSiblings(
        string excludeMailbox, string excludeUid, IReadOnlyList<string>? references, int max = 200)
    {
        var ancestorIds = references?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList() ?? [];
        if (ancestorIds.Count == 0)
            return [];

        lock (_readGate)
        {
            using var cmd = _readConn.CreateCommand();
            var placeholders = string.Join(",", ancestorIds.Select((_, i) => $"$anc{i}"));

            cmd.CommandText = $"""
                SELECT DISTINCT m.mailbox, m.uid, m.message_id, m.subject, m.sender_name, m.sender_address,
                       m.snippet, m.date_utc, m.unread, m.starred, m.has_attachment
                FROM messages m
                WHERE m.message_id IN ({placeholders})
                ORDER BY m.date_utc DESC LIMIT $limit;
                """;
            for (var i = 0; i < ancestorIds.Count; i++)
                cmd.Parameters.AddWithValue($"$anc{i}", ancestorIds[i]);
            cmd.Parameters.AddWithValue("$limit", max + 1); // +1 to allow for excluding the opened message below

            var rows = new List<InboxRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var mailbox = reader.GetString(0);
                var uidText = reader.GetInt64(1).ToString();
                if (mailbox == excludeMailbox && uidText == excludeUid)
                    continue;
                rows.Add(ReadRowFrom(reader, offset: 1, mailbox == excludeMailbox ? null : mailbox));
            }
            return rows.Take(max).ToList();
        }
    }

    // ---- Contacts -------------------------------------------------------------------------------

    public IReadOnlyList<ContactEntry> SuggestContacts(string query, int max = 6)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        lock (_readGate)
        {
            using var cmd = _readConn.CreateCommand();
            cmd.CommandText = """
                SELECT address, display_name, score FROM contacts
                WHERE address LIKE $like ESCAPE '\' OR display_name LIKE $like ESCAPE '\'
                ORDER BY score DESC LIMIT $max;
                """;
            cmd.Parameters.AddWithValue("$like", "%" + LikeEscape(query) + "%");
            cmd.Parameters.AddWithValue("$max", max);
            using var reader = cmd.ExecuteReader();
            var result = new List<ContactEntry>();
            while (reader.Read())
                result.Add(ToContactEntry(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            return result;
        }
    }

    public ContactEntry? FindContact(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;
        lock (_readGate)
        {
            using var cmd = _readConn.CreateCommand();
            cmd.CommandText = "SELECT address, display_name, score FROM contacts WHERE address = $address COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$address", address);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ToContactEntry(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)) : null;
        }
    }

    public IReadOnlyList<ContactEntry> ListAllContacts()
    {
        lock (_readGate)
        {
            using var cmd = _readConn.CreateCommand();
            cmd.CommandText = "SELECT address, display_name, score FROM contacts ORDER BY score DESC, display_name COLLATE NOCASE;";
            using var reader = cmd.ExecuteReader();
            var result = new List<ContactEntry>();
            while (reader.Read())
                result.Add(ToContactEntry(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            return result;
        }
    }

    /// <summary>Increments one address's correspondence score, recording a display name the first
    /// time a real one is seen (never overwriting a known name with a blank one) — same "first name
    /// wins" rule ContactsIndex.ScanAsync already used.</summary>
    public void RecordCorrespondence(string address, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(address))
            return;
        lock (_writeGate)
        {
            using var cmd = _writeConn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO contacts (address, display_name, score, last_seen_utc)
                VALUES ($address, $name, 1, $now)
                ON CONFLICT(address) DO UPDATE SET
                  display_name = CASE WHEN contacts.display_name = '' THEN excluded.display_name ELSE contacts.display_name END,
                  score = contacts.score + 1, last_seen_utc = excluded.last_seen_utc;
                """;
            cmd.Parameters.AddWithValue("$address", address);
            cmd.Parameters.AddWithValue("$name", displayName ?? "");
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Seeds/refreshes a contact from an authoritative external count (ContactsIndex's own
    /// IMAP envelope scan) — unlike <see cref="RecordCorrespondence"/>, which always adds exactly
    /// one, this sets the score directly. MAX rather than a plain overwrite: a re-seed (e.g. every
    /// app start, until ContactsIndex's own scan is retired in favor of incremental sync-driven
    /// updates) must not regress a higher score this session's own organic correspondence already
    /// recorded since the last seed.</summary>
    public void SeedContact(string address, string? displayName, int score)
    {
        if (string.IsNullOrWhiteSpace(address))
            return;
        lock (_writeGate)
        {
            using var cmd = _writeConn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO contacts (address, display_name, score, last_seen_utc)
                VALUES ($address, $name, $score, $now)
                ON CONFLICT(address) DO UPDATE SET
                  display_name = CASE WHEN contacts.display_name = '' THEN excluded.display_name ELSE contacts.display_name END,
                  score = MAX(contacts.score, excluded.score), last_seen_utc = excluded.last_seen_utc;
                """;
            cmd.Parameters.AddWithValue("$address", address);
            cmd.Parameters.AddWithValue("$name", displayName ?? "");
            cmd.Parameters.AddWithValue("$score", score);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    private static ContactEntry ToContactEntry(string address, string displayName, int score) => new(
        string.IsNullOrWhiteSpace(displayName) ? address : displayName,
        address,
        string.IsNullOrWhiteSpace(displayName) ? address : $"{displayName} <{address}>",
        score);

    private static string LikeEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ---- Row materialization helpers --------------------------------------------------------------

    /// <summary>Reads one row from a `messages` SELECT whose column order matches GetMessages' own
    /// query above (uid, message_id, subject, sender_name, sender_address, snippet, date_utc,
    /// unread, starred, has_attachment starting at column 0).</summary>
    private static InboxRow ReadRow(SqliteDataReader reader, string? mailbox)
    {
        var when = DateTime.Parse(reader.GetString(6)).ToLocalTime();
        return new InboxRow(
            Id: reader.GetInt64(0).ToString(),
            Sender: reader.GetString(3),
            Subject: reader.GetString(2),
            Snippet: reader.GetString(5),
            Date: MailText.FormatListDate(when),
            Unread: reader.GetInt32(7) != 0,
            Starred: reader.GetInt32(8) != 0,
            HasAttachment: reader.GetInt32(9) != 0,
            Timestamp: when,
            SenderAddress: reader.GetString(4),
            Mailbox: mailbox);
    }

    /// <summary>Same as <see cref="ReadRow"/> but for a SELECT that has extra leading columns (e.g.
    /// a "mailbox" column already consumed by the caller) — offset shifts where the shared field
    /// layout (uid, message_id, subject, ...) actually starts.</summary>
    private static InboxRow ReadRowFrom(SqliteDataReader reader, int offset, string? mailbox)
    {
        var when = DateTime.Parse(reader.GetString(offset + 6)).ToLocalTime();
        return new InboxRow(
            Id: reader.GetInt64(offset).ToString(),
            Sender: reader.GetString(offset + 3),
            Subject: reader.GetString(offset + 2),
            Snippet: reader.GetString(offset + 5),
            Date: MailText.FormatListDate(when),
            Unread: reader.GetInt32(offset + 7) != 0,
            Starred: reader.GetInt32(offset + 8) != 0,
            HasAttachment: reader.GetInt32(offset + 9) != 0,
            Timestamp: when,
            SenderAddress: reader.GetString(offset + 4),
            Mailbox: mailbox);
    }

    /// <summary>Only ever called from within GetMessages' own _readGate lock, against _readConn.</summary>
    private int ScalarInt(string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = _readConn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string? Serialize<T>(T? value) => value is null ? null : JsonSerializer.Serialize(value);

    private static T? DeserializeOrNull<T>(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? default : JsonSerializer.Deserialize<T>(reader.GetString(column));

    public void Dispose()
    {
        lock (_writeGate)
            _writeConn.Dispose();
        lock (_readGate)
            _readConn.Dispose();
    }
}
