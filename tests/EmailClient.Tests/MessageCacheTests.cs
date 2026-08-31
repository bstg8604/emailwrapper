using System.IO;
using EmailClient.Automation;
using EmailClient.Mail;
using Xunit;

namespace EmailClient.Tests;

public class MessageCacheTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "purplemail-cache-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private MessageCache Cache(string email = "a@b.c") => new(email, _root);

    private static InboxRow Row(string id, string sender = "Ada", string subject = "Hi") =>
        new(id, sender, subject, "snippet", "9:00 AM", Unread: true);

    [Fact]
    public void Rows_RoundTrip()
    {
        var cache = Cache();
        cache.SaveRows("Inbox", [Row("1"), Row("2", "Bob", "Later")]);

        var loaded = cache.LoadRows("Inbox");

        Assert.Equal(2, loaded.Count);
        Assert.Equal("Bob", loaded[1].Sender);
        Assert.Equal("Later", loaded[1].Subject);
    }

    [Fact]
    public void LoadRows_ForAnUncachedMailbox_IsEmptyNotAnError()
    {
        Assert.Empty(Cache().LoadRows("Nonexistent"));
    }

    [Fact]
    public void Mailboxes_DoNotAnswerForEachOther()
    {
        var cache = Cache();
        cache.SaveRows("Inbox", [Row("1")]);
        cache.SaveRows("Trash", [Row("9"), Row("8")]);

        Assert.Single(cache.LoadRows("Inbox"));
        Assert.Equal(2, cache.LoadRows("Trash").Count);
    }

    [Fact]
    public void Accounts_DoNotAnswerForEachOther()
    {
        Cache("one@x.com").SaveRows("Inbox", [Row("1")]);

        Assert.Empty(Cache("two@x.com").LoadRows("Inbox"));
    }

    [Fact]
    public void MailboxNamesWithPathCharacters_AreStillUsable()
    {
        // IMAP mailbox names are user-defined and hierarchical: "INBOX/Work" would be a directory
        // traversal if it were used as a filename directly.
        var cache = Cache();
        cache.SaveRows("INBOX/Work", [Row("1")]);

        Assert.Single(cache.LoadRows("INBOX/Work"));
        Assert.Empty(cache.LoadRows("INBOX/Other"));
    }

    [Fact]
    public void Detail_RoundTrips()
    {
        var cache = Cache();
        var detail = new MessageDetail("Subject", "Ada", "Today", "<p>Body</p>", To: "me@x.com");

        cache.SaveDetail("42", detail);
        var loaded = cache.LoadDetail("42");

        Assert.NotNull(loaded);
        Assert.Equal("<p>Body</p>", loaded!.BodyHtml);
        Assert.Equal("me@x.com", loaded.To);
    }

    [Fact]
    public void LoadDetail_ForAnUncachedMessage_IsNull()
    {
        Assert.Null(Cache().LoadDetail("nope"));
    }

    [Fact]
    public void Clear_RemovesEverythingForThatAccount()
    {
        var cache = Cache();
        cache.SaveRows("Inbox", [Row("1")]);
        cache.SaveDetail("1", new MessageDetail("s", "f", "d", "b"));

        cache.Clear();

        Assert.Empty(cache.LoadRows("Inbox"));
        Assert.Null(cache.LoadDetail("1"));
    }

    [Fact]
    public void Clear_OnAnEmptyCache_DoesNotThrow()
    {
        Cache().Clear();
    }
}
