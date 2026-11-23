using System.IO;
using EmailClient.Automation;
using EmailClient.Mail;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EmailClient.Tests;

public class MailStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "purplemail-mailstore-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private MailStore Store(string email = "a@b.c") => new(email, _root);

    private static InboxRow Row(string id, string sender = "Ada", string subject = "Hi",
        string senderAddress = "ada@x.com", DateTime? when = null, bool unread = true, bool starred = false,
        bool hasAttachment = false) =>
        new(id, sender, subject, "snippet", "9:00 AM", Unread: unread, Starred: starred,
            HasAttachment: hasAttachment, Timestamp: when ?? DateTime.UtcNow, SenderAddress: senderAddress);

    // ---- Messages / paging -----------------------------------------------------------------

    [Fact]
    public void UpsertAndGetMessages_RoundTrips()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", "Ada", "Hi"), Row("2", "Bob", "Later")]);

        var page = store.GetMessages("Inbox", 1);

        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Rows.Count);
    }

    [Fact]
    public void GetMessages_NewestFirst()
    {
        using var store = Store();
        var older = DateTime.UtcNow.AddDays(-1);
        var newer = DateTime.UtcNow;
        store.UpsertMessages("Inbox", [Row("1", "Ada", "Old", when: older), Row("2", "Bob", "New", when: newer)]);

        var page = store.GetMessages("Inbox", 1);

        Assert.Equal("New", page.Rows[0].Subject);
        Assert.Equal("Old", page.Rows[1].Subject);
    }

    [Fact]
    public void GetMessages_ForAnUnsyncedMailbox_IsEmptyNotAnError()
    {
        using var store = Store();
        var page = store.GetMessages("Nonexistent", 1);
        Assert.Empty(page.Rows);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public void Mailboxes_DoNotAnswerForEachOther()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1")]);
        store.UpsertMessages("Trash", [Row("9"), Row("8")]);

        Assert.Single(store.GetMessages("Inbox", 1).Rows);
        Assert.Equal(2, store.GetMessages("Trash", 1).Rows.Count);
    }

    [Fact]
    public void Accounts_DoNotAnswerForEachOther()
    {
        using (var store = Store("one@x.com"))
            store.UpsertMessages("Inbox", [Row("1")]);

        using var other = Store("two@x.com");
        Assert.Empty(other.GetMessages("Inbox", 1).Rows);
    }

    [Fact]
    public void UpsertMessages_SameUidTwice_UpdatesRatherThanDuplicates()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "First", unread: true)]);
        store.UpsertMessages("Inbox", [Row("1", subject: "First edited", unread: false)]);

        var page = store.GetMessages("Inbox", 1);

        Assert.Single(page.Rows);
        Assert.Equal("First edited", page.Rows[0].Subject);
        Assert.False(page.Rows[0].Unread);
    }

    [Fact]
    public void UpsertMessages_EmptyList_DoesNotThrow()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", []);
        Assert.Empty(store.GetMessages("Inbox", 1).Rows);
    }

    [Fact]
    public void GetMessages_SameFolderRows_HaveNullMailbox()
    {
        // Matches every existing row's own convention: null Mailbox means "whatever folder is
        // currently open" — only a genuinely cross-folder result (FindConversationSiblings) sets it.
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1")]);
        Assert.Null(store.GetMessages("Inbox", 1).Rows[0].Mailbox);
    }

    [Fact]
    public void UpdateFlags_ChangesUnreadAndStarredIndependently()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", unread: true, starred: false)]);

        store.UpdateFlags("Inbox", "1", starred: true);
        var afterStar = store.GetMessages("Inbox", 1).Rows[0];
        Assert.True(afterStar.Unread); // untouched
        Assert.True(afterStar.Starred);

        store.UpdateFlags("Inbox", "1", unread: false);
        var afterRead = store.GetMessages("Inbox", 1).Rows[0];
        Assert.False(afterRead.Unread);
        Assert.True(afterRead.Starred); // untouched by the second call
    }

    [Fact]
    public void UpdateFlags_DoesNotWipeOutAPreviouslyRecordedMessageId()
    {
        // The real bug this guards: a flags-only upsert must COALESCE message_id, not overwrite it
        // with null — otherwise every mark-read/star silently breaks conversation-sibling lookup.
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1")], new Dictionary<string, string?> { ["1"] = "<abc@x.com>" });

        store.UpdateFlags("Inbox", "1", unread: false);

        // If message_id survived, an ancestor search for a hypothetical reply (whose own
        // References would contain "<abc@x.com>") should still find message "1".
        var siblings = store.FindConversationSiblings("Other", "999", references: ["<abc@x.com>"]);
        Assert.Single(siblings);
    }

    [Fact]
    public void DeleteMessage_RemovesIt()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1"), Row("2")]);

        store.DeleteMessage("Inbox", "1");

        var page = store.GetMessages("Inbox", 1);
        Assert.Single(page.Rows);
        Assert.Equal("2", page.Rows[0].Id);
    }

    // ---- Bodies -----------------------------------------------------------------------------

    [Fact]
    public void Body_RoundTrips_IncludingNestedFields()
    {
        using var store = Store();
        var detail = new MessageDetail(
            "Subject", "Ada <ada@x.com>", "Today", "<p>Body</p>", To: "me@x.com",
            Attachments: [new MailAttachment("file.pdf", "1 KB", "imap:1:0")],
            Calendar: new CalendarInvite("Standup", DateTimeOffset.UtcNow, "Room 1", "https://zoom.us/x"),
            MessageId: "<abc@x.com>",
            References: ["<parent@x.com>"]);

        store.SaveBody("Inbox", "42", detail);
        var loaded = store.GetBody("Inbox", "42");

        Assert.NotNull(loaded);
        Assert.Equal("<p>Body</p>", loaded!.BodyHtml);
        Assert.Equal("me@x.com", loaded.To);
        Assert.Single(loaded.Attachments!);
        Assert.Equal("file.pdf", loaded.Attachments![0].Name);
        Assert.Equal("Standup", loaded.Calendar!.Title);
        Assert.Equal("<abc@x.com>", loaded.MessageId);
        Assert.Single(loaded.References!);
    }

    [Fact]
    public void Body_WithNoNestedFields_RoundTripsWithoutError()
    {
        using var store = Store();
        var detail = new MessageDetail("s", "f", "d", "b");

        store.SaveBody("Inbox", "1", detail);
        var loaded = store.GetBody("Inbox", "1");

        Assert.NotNull(loaded);
        Assert.Null(loaded!.Attachments);
        Assert.Null(loaded.Calendar);
        Assert.Null(loaded.MessageId);
    }

    [Fact]
    public void GetBody_ForAnUncachedMessage_IsNull()
    {
        using var store = Store();
        Assert.Null(store.GetBody("Inbox", "nope"));
    }

    [Fact]
    public void SaveBody_SameKeyTwice_Overwrites()
    {
        using var store = Store();
        store.SaveBody("Inbox", "1", new MessageDetail("s", "f", "d", "<p>First</p>"));
        store.SaveBody("Inbox", "1", new MessageDetail("s", "f", "d", "<p>Second</p>"));

        Assert.Equal("<p>Second</p>", store.GetBody("Inbox", "1")!.BodyHtml);
    }

    [Fact]
    public void Body_SameUidDifferentMailbox_DoesNotCollide()
    {
        // The real bug this guards: a UID is only unique within its own folder — Inbox UID 1 and
        // Sent UID 1 are unrelated messages, so their bodies must not answer for each other.
        using var store = Store();
        store.SaveBody("Inbox", "1", new MessageDetail("s", "f", "d", "<p>Inbox copy</p>"));
        store.SaveBody("Sent", "1", new MessageDetail("s", "f", "d", "<p>Sent copy</p>"));

        Assert.Equal("<p>Inbox copy</p>", store.GetBody("Inbox", "1")!.BodyHtml);
        Assert.Equal("<p>Sent copy</p>", store.GetBody("Sent", "1")!.BodyHtml);
    }

    [Fact]
    public void SaveBody_ComputesPlainTextForSearch_StrippingHtmlTags()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Unrelated subject")]);
        store.SaveBody("Inbox", "1", new MessageDetail("s", "f", "d", "<p>Quarterly <b>budget</b> review</p>"));

        var results = store.Search("budget");

        Assert.Single(results);
        Assert.Equal("1", results[0].Id);
    }

    [Fact]
    public void Search_MatchesBodyTextEvenWithoutSubjectMatch()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Meeting notes")]);
        store.SaveBody("Inbox", "1", new MessageDetail("s", "f", "d", "<p>Discussed the marketing budget</p>"));

        Assert.Single(store.Search("marketing"));
        Assert.Empty(store.Search("marketing", mailbox: "Sent")); // scoped to the wrong folder
    }

    [Fact]
    public void Search_UnopenedMessage_HasNoBodyMatchButStillMatchesSubject()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Budget notes")]);
        // No SaveBody call — this message has never been opened/backfilled.

        Assert.Single(store.Search("Budget")); // subject still matches
        Assert.Empty(store.Search("some body text nobody indexed"));
    }

    [Fact]
    public void GetUidsMissingBody_OnlyListsSyncedMessagesWithoutABody()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1"), Row("2"), Row("3")]);
        store.SaveBody("Inbox", "2", new MessageDetail("s", "f", "d", "b"));

        var missing = store.GetUidsMissingBody("Inbox", 10);

        Assert.Equal(2, missing.Count);
        Assert.Contains("1", missing);
        Assert.Contains("3", missing);
        Assert.DoesNotContain("2", missing);
    }

    [Fact]
    public void GetUidsMissingBody_RespectsLimit()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1"), Row("2"), Row("3")]);

        Assert.Equal(2, store.GetUidsMissingBody("Inbox", 2).Count);
    }

    [Fact]
    public void GetUidsMissingBody_EmptyFolder_ReturnsEmptyNotError()
    {
        using var store = Store();
        Assert.Empty(store.GetUidsMissingBody("Nonexistent", 10));
    }

    [Fact]
    public void CountUidsMissingBody_CountsCorrectly()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1"), Row("2"), Row("3")]);
        store.SaveBody("Inbox", "2", new MessageDetail("s", "f", "d", "b"));

        Assert.Equal(2, store.CountUidsMissingBody("Inbox"));
    }

    [Fact]
    public void CountUidsMissingBody_FullyBackfilledFolder_ReturnsZero()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1")]);
        store.SaveBody("Inbox", "1", new MessageDetail("s", "f", "d", "b"));

        Assert.Equal(0, store.CountUidsMissingBody("Inbox"));
    }

    [Fact]
    public void HasMessageId_KnownId_ReturnsTrue()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1")], new Dictionary<string, string?> { ["1"] = "<a@x.com>" });

        Assert.True(store.HasMessageId("<a@x.com>"));
    }

    [Fact]
    public void HasMessageId_UnknownId_ReturnsFalse()
    {
        using var store = Store();
        Assert.False(store.HasMessageId("<nobody@x.com>"));
    }

    [Fact]
    public void DeleteMessage_RemovesItsBodyFromSearchToo()
    {
        // The body_fts trigger chain (bodies_ad) must fire even though message_bodies rows aren't
        // deleted by DeleteMessage directly — this confirms deleting the *message* row alone
        // doesn't leave an orphaned, still-searchable body indexed under a UID that no longer exists.
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Keepable")]);
        store.SaveBody("Inbox", "1", new MessageDetail("s", "f", "d", "<p>unique-marker-text</p>"));
        Assert.Single(store.Search("unique-marker-text"));

        store.DeleteMessage("Inbox", "1");

        // The message row is gone, so it can no longer surface via the messages JOIN even though
        // the orphaned body row (SQLite has no cascading delete configured here) still exists.
        Assert.Empty(store.Search("unique-marker-text"));
    }

    // ---- Folders ----------------------------------------------------------------------------

    [Fact]
    public void Folders_RoundTrip_OrderedByDepthThenName()
    {
        using var store = Store();
        store.UpsertFolder(new MailFolder("INBOX", "Inbox", 3, 0));
        store.UpsertFolder(new MailFolder("INBOX/Work", "Work", 1, 1));

        var folders = store.GetFolders();

        Assert.Equal(2, folders.Count);
        Assert.Equal("Inbox", folders[0].Name);
        Assert.Equal(3, folders[0].Unread);
    }

    [Fact]
    public void UpsertFolder_SameMailboxTwice_UpdatesUnreadCount()
    {
        using var store = Store();
        store.UpsertFolder(new MailFolder("INBOX", "Inbox", 5, 0));
        store.UpsertFolder(new MailFolder("INBOX", "Inbox", 0, 0));

        Assert.Equal(0, store.GetFolders().Single().Unread);
    }

    [Fact]
    public void UpsertFolder_RoundTripsTotal()
    {
        using var store = Store();
        store.UpsertFolder(new MailFolder("INBOX", "Inbox", 3, 0, Total: 4700));

        Assert.Equal(4700, store.GetFolders().Single().Total);
        Assert.Equal(4700, store.GetFolderTotal("INBOX"));
    }

    [Fact]
    public void GetFolderTotal_UnknownMailbox_ReturnsZero()
    {
        using var store = Store();
        Assert.Equal(0, store.GetFolderTotal("Nonexistent"));
    }

    [Fact]
    public void ExistingDatabaseWithoutTotalCountColumn_MigratesCleanlyOnOpen()
    {
        // Simulates a real user's database from before MailFolder.Total existed — opening it must
        // not throw, and total_count must actually be usable afterward (not just silently absent).
        var email = "old-schema@x.com";
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(email.ToLowerInvariant())), 0, 10);
        var dir = Path.Combine(_root, hash);
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "mail.db");

        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE folders (
                  mailbox TEXT PRIMARY KEY, display_name TEXT NOT NULL,
                  unread_count INTEGER NOT NULL, depth INTEGER NOT NULL, last_synced_utc TEXT
                );
                INSERT INTO folders (mailbox, display_name, unread_count, depth) VALUES ('INBOX', 'Inbox', 3, 0);
                """;
            cmd.ExecuteNonQuery();
        }

        using var store = new MailStore(email, _root);

        // The migrated column defaults to 0 for a pre-existing row (real total genuinely unknown
        // yet), and is fully usable for a fresh write afterward.
        Assert.Equal(0, store.GetFolderTotal("INBOX"));
        store.UpsertFolder(new MailFolder("INBOX", "Inbox", 5, 0, Total: 42));
        Assert.Equal(42, store.GetFolderTotal("INBOX"));
    }

    // ---- Search -----------------------------------------------------------------------------

    [Fact]
    public void Search_MatchesSubject()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Teachers' Day Celebration"), Row("2", subject: "Unrelated")]);

        var results = store.Search("Teachers");

        Assert.Single(results);
        Assert.Equal("1", results[0].Id);
    }

    [Fact]
    public void Search_MatchesSenderName()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", sender: "PRO IIT Bombay", subject: "Notice")]);

        Assert.Single(store.Search("Bombay"));
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Important Notice")]);

        Assert.Single(store.Search("important"));
    }

    [Fact]
    public void Search_ApostropheInSubject_DoesNotThrow()
    {
        // Real data: "Teachers' Day", "Principal's Office" — apostrophes are common and must not
        // break the FTS5 query (which uses ' for its own string literals) or the SQL parameters.
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Principal's Office Notice")]);

        var results = store.Search("Principal's");

        Assert.Single(results);
    }

    [Fact]
    public void Search_DoubleQuoteInQuery_DoesNotThrow()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Say \"hello\" to everyone")]);

        // Not asserting a match here — just that a literal double-quote (FTS5's own phrase
        // delimiter) in the search text doesn't throw a syntax error.
        var results = store.Search("\"weird");

        Assert.NotNull(results);
    }

    [Fact]
    public void Search_NoMatches_ReturnsEmptyNotError()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Hello")]);

        Assert.Empty(store.Search("zzz-nonexistent-zzz"));
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Hello")]);

        Assert.Empty(store.Search(""));
    }

    [Fact]
    public void Search_UpdatedSubject_ReflectsInIndex()
    {
        // The FTS index is trigger-maintained on INSERT/UPDATE/DELETE — verify an update actually
        // re-indexes rather than leaving a stale entry searchable under the old subject.
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Original Subject")]);
        Assert.Single(store.Search("Original"));

        store.UpsertMessages("Inbox", [Row("1", subject: "Renamed Subject")]);

        Assert.Empty(store.Search("Original"));
        Assert.Single(store.Search("Renamed"));
    }

    [Fact]
    public void Search_DeletedMessage_NoLongerMatches()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Findable")]);
        store.DeleteMessage("Inbox", "1");

        Assert.Empty(store.Search("Findable"));
    }

    // ---- Conversation siblings ----------------------------------------------------------------

    [Fact]
    public void FindConversationSiblings_MatchesByMessageId()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Original")],
            new Dictionary<string, string?> { ["1"] = "<a@x.com>" });
        store.UpsertMessages("Sent", [Row("2", subject: "Re: Original")],
            new Dictionary<string, string?> { ["2"] = "<b@x.com>" });

        // The reply (in Sent) references the original's Message-ID.
        var siblings = store.FindConversationSiblings("Sent", "2", ["<a@x.com>"]);

        Assert.Single(siblings);
        Assert.Equal("1", siblings[0].Id);
    }

    [Fact]
    public void FindConversationSiblings_TagsCrossFolderResultWithItsMailbox()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1")], new Dictionary<string, string?> { ["1"] = "<a@x.com>" });

        var siblings = store.FindConversationSiblings("Sent", "2", ["<a@x.com>"]);

        Assert.Equal("Inbox", siblings[0].Mailbox);
    }

    [Fact]
    public void FindConversationSiblings_SameFolderResult_HasNullMailbox()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1")], new Dictionary<string, string?> { ["1"] = "<a@x.com>" });
        store.UpsertMessages("Inbox", [Row("2")], new Dictionary<string, string?> { ["2"] = "<b@x.com>" });

        var siblings = store.FindConversationSiblings("Inbox", "2", ["<a@x.com>"]);

        Assert.Null(siblings[0].Mailbox);
    }

    [Fact]
    public void FindConversationSiblings_ExcludesTheOpenedMessageItself()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1")], new Dictionary<string, string?> { ["1"] = "<a@x.com>" });

        // Message "1" pathologically references its own message_id — searching for its own
        // ancestors must still not return "1" as its own sibling.
        var siblings = store.FindConversationSiblings("Inbox", "1", ["<a@x.com>"]);

        Assert.Empty(siblings);
    }

    [Fact]
    public void FindConversationSiblings_NoReferences_ReturnsEmpty()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1")]);

        Assert.Empty(store.FindConversationSiblings("Inbox", "1", null));
    }

    // Ancestors only — messages the opened one replied to — never the reverse (messages that
    // reply *to* the opened one). An earlier version of this method also matched the reply
    // direction, added specifically so a reply would show the full thread; a live report
    // corrected that: opening an *earlier* message in a thread must not show a later reply that
    // didn't exist yet from that message's own point of view. See MailStore.FindConversationSiblings'
    // own remarks.

    [Fact]
    public void SaveBody_BackfillsMessageId_WhenSyncedWithoutOne()
    {
        // A row inserted by the cold-start live-fallback (no messageIds dict, see
        // RefreshMessagesAsync's own remarks) should still get its message_id filled in once the
        // message is actually opened, so it can be found as an ancestor afterward.
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Original")]); // no messageIds

        store.SaveBody("Inbox", "1", new MessageDetail("Original", "f", "d", "b", MessageId: "<a@x.com>"));

        var siblings = store.FindConversationSiblings("Other", "999", references: ["<a@x.com>"]);
        Assert.Single(siblings);
        Assert.Equal("1", siblings[0].Id);
    }

    [Fact]
    public void SaveBody_DoesNotOverwriteAnAlreadyKnownMessageId()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1")], new Dictionary<string, string?> { ["1"] = "<original@x.com>" });

        // A stale/different id from a body fetch must not clobber the one already recorded.
        store.SaveBody("Inbox", "1", new MessageDetail("s", "f", "d", "b", MessageId: "<different@x.com>"));

        Assert.Single(store.FindConversationSiblings("Other", "999", references: ["<original@x.com>"]));
        Assert.Empty(store.FindConversationSiblings("Other", "999", references: ["<different@x.com>"]));
    }

    // ---- Reply status -----------------------------------------------------------------------

    [Fact]
    public void AttachReplyStatus_InboxMessageWithSentReply_SetsReplied()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1", subject: "Original")],
            new Dictionary<string, string?> { ["1"] = "<a@x.com>" });
        // The reply, sitting in Sent, references the Inbox message's own id.
        store.UpsertMessages("Sent", [Row("2", subject: "Re: Original")],
            new Dictionary<string, string?> { ["2"] = "<b@x.com>" },
            new Dictionary<string, IReadOnlyList<string>?> { ["2"] = ["<a@x.com>"] });

        var rows = store.AttachReplyStatus([Row("1")], "Inbox", sentMailbox: "Sent");

        Assert.True(rows[0].Replied);
        Assert.False(rows[0].HasReply);
    }

    [Fact]
    public void AttachReplyStatus_SentMessageWithInboxReply_SetsHasReply()
    {
        using var store = Store();
        store.UpsertMessages("Sent", [Row("1", subject: "Original")],
            new Dictionary<string, string?> { ["1"] = "<a@x.com>" });
        // The reply, sitting in Inbox, references the Sent message's own id.
        store.UpsertMessages("Inbox", [Row("2", subject: "Re: Original")],
            new Dictionary<string, string?> { ["2"] = "<b@x.com>" },
            new Dictionary<string, IReadOnlyList<string>?> { ["2"] = ["<a@x.com>"] });

        var rows = store.AttachReplyStatus([Row("1")], "Sent", sentMailbox: "Sent");

        Assert.True(rows[0].HasReply);
        Assert.False(rows[0].Replied);
    }

    [Fact]
    public void AttachReplyStatus_NoReply_LeavesBothFlagsFalse()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1")], new Dictionary<string, string?> { ["1"] = "<a@x.com>" });

        var rows = store.AttachReplyStatus([Row("1")], "Inbox", sentMailbox: "Sent");

        Assert.False(rows[0].Replied);
        Assert.False(rows[0].HasReply);
    }

    [Fact]
    public void AttachReplyStatus_UnknownMessageId_DoesNotThrow()
    {
        using var store = Store();
        store.UpsertMessages("Inbox", [Row("1")]); // no messageIds — message_id stays null

        var rows = store.AttachReplyStatus([Row("1")], "Inbox", sentMailbox: "Sent");

        Assert.False(rows[0].Replied);
        Assert.False(rows[0].HasReply);
    }

    // ---- Contacts ---------------------------------------------------------------------------

    [Fact]
    public void SeedContact_SetsScoreDirectly_NotIncrementally()
    {
        using var store = Store();
        store.SeedContact("ada@x.com", "Ada Lovelace", 47);

        Assert.Equal(47, store.FindContact("ada@x.com")!.Value.Score);
    }

    [Fact]
    public void SeedContact_DoesNotRegressAHigherExistingScore()
    {
        // A re-seed (e.g. every app start, until ContactsIndex's own scan is retired) must not
        // undo organic correspondence this session already recorded since the last seed.
        using var store = Store();
        store.RecordCorrespondence("ada@x.com", "Ada Lovelace");
        store.RecordCorrespondence("ada@x.com", "Ada Lovelace");
        store.RecordCorrespondence("ada@x.com", "Ada Lovelace"); // score is now 3

        store.SeedContact("ada@x.com", "Ada Lovelace", 1); // a stale/lower external count

        Assert.Equal(3, store.FindContact("ada@x.com")!.Value.Score);
    }

    [Fact]
    public void SeedContact_RaisesAScoreThatWasLower()
    {
        using var store = Store();
        store.RecordCorrespondence("ada@x.com", "Ada Lovelace"); // score is 1

        store.SeedContact("ada@x.com", "Ada Lovelace", 50);

        Assert.Equal(50, store.FindContact("ada@x.com")!.Value.Score);
    }

    [Fact]
    public void SeedContact_DoesNotOverwriteKnownNameWithBlank()
    {
        using var store = Store();
        store.RecordCorrespondence("ada@x.com", "Ada Lovelace");

        store.SeedContact("ada@x.com", null, 10);

        Assert.Equal("Ada Lovelace", store.FindContact("ada@x.com")!.Value.DisplayName);
    }

    [Fact]
    public void RecordCorrespondence_IncrementsScoreOnRepeat()
    {
        using var store = Store();
        store.RecordCorrespondence("ada@x.com", "Ada Lovelace");
        store.RecordCorrespondence("ada@x.com", "Ada Lovelace");

        var contact = store.FindContact("ada@x.com");

        Assert.NotNull(contact);
        Assert.Equal(2, contact!.Value.Score);
    }

    [Fact]
    public void RecordCorrespondence_DoesNotOverwriteKnownNameWithBlank()
    {
        using var store = Store();
        store.RecordCorrespondence("ada@x.com", "Ada Lovelace");
        store.RecordCorrespondence("ada@x.com", null);

        Assert.Equal("Ada Lovelace", store.FindContact("ada@x.com")!.Value.DisplayName);
    }

    [Fact]
    public void RecordCorrespondence_LaterRealNameFillsInABlankOne()
    {
        using var store = Store();
        store.RecordCorrespondence("ada@x.com", null);
        store.RecordCorrespondence("ada@x.com", "Ada Lovelace");

        Assert.Equal("Ada Lovelace", store.FindContact("ada@x.com")!.Value.DisplayName);
    }

    [Fact]
    public void SuggestContacts_MatchesSubstringOfNameOrAddress()
    {
        using var store = Store();
        store.RecordCorrespondence("ada@x.com", "Ada Lovelace");
        store.RecordCorrespondence("bob@x.com", "Bob Smith");

        Assert.Single(store.SuggestContacts("Love"));
        Assert.Single(store.SuggestContacts("bob@"));
    }

    [Fact]
    public void ListAllContacts_OrdersByScoreDescending()
    {
        using var store = Store();
        store.RecordCorrespondence("bob@x.com", "Bob");
        store.RecordCorrespondence("ada@x.com", "Ada");
        store.RecordCorrespondence("ada@x.com", "Ada");
        store.RecordCorrespondence("ada@x.com", "Ada");

        var all = store.ListAllContacts();

        Assert.Equal("ada@x.com", all[0].Address);
    }

    [Fact]
    public void FindContact_IsCaseInsensitive()
    {
        using var store = Store();
        store.RecordCorrespondence("Ada@X.com", "Ada");

        Assert.NotNull(store.FindContact("ada@x.com"));
    }

    [Fact]
    public void FindContact_Unknown_ReturnsNull()
    {
        using var store = Store();
        Assert.Null(store.FindContact("nobody@x.com"));
    }

    [Fact]
    public void ListAllContacts_Empty_ReturnsEmptyNotError()
    {
        using var store = Store();
        Assert.Empty(store.ListAllContacts());
    }

    [Fact]
    public void SuggestContacts_EmptyQuery_ReturnsEmpty()
    {
        using var store = Store();
        store.RecordCorrespondence("ada@x.com", "Ada");
        Assert.Empty(store.SuggestContacts(""));
    }

    [Fact]
    public void SuggestContacts_QueryWithLikeWildcardCharacters_IsTreatedLiterally()
    {
        // '%' and '_' are SQL LIKE metacharacters — a contact whose name/address doesn't actually
        // contain a wildcard must not match a query that merely contains one, and a query
        // containing them must not throw or behave as an unintended wildcard search.
        using var store = Store();
        store.RecordCorrespondence("ada@x.com", "Ada Lovelace");

        Assert.Empty(store.SuggestContacts("%"));
        Assert.Empty(store.SuggestContacts("_"));
    }
}
