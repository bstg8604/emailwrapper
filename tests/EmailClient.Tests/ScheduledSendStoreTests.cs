using System.IO;
using EmailClient.Mail;
using EmailClient.UI;
using Xunit;

namespace EmailClient.Tests;

public class ScheduledSendStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "purplemail-scheduled-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ScheduledSendStore Store(string email = "a@b.c") => new(email, _root);

    private static ComposeResult Result(string to = "bob@example.com", string subject = "Hi") =>
        new(to, "", "", subject, "Body");

    [Fact]
    public void Add_ThenLoad_RoundTrips()
    {
        var store = Store();
        var sendAt = DateTime.UtcNow.AddHours(1);

        var id = store.Add(Result(subject: "Later"), sendAt);
        var loaded = store.Load();

        Assert.Single(loaded);
        Assert.Equal(id, loaded[0].Id);
        Assert.Equal("Later", loaded[0].Result.Subject);
        Assert.Equal(sendAt, loaded[0].SendAtUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Remove_ExistingId_ReturnsTrue_AndDropsIt()
    {
        var store = Store();
        var id = store.Add(Result(), DateTime.UtcNow.AddHours(1));

        Assert.True(store.Remove(id));
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Remove_UnknownId_ReturnsFalse_AndLeavesOthersAlone()
    {
        var store = Store();
        store.Add(Result(), DateTime.UtcNow.AddHours(1));

        Assert.False(store.Remove("does-not-exist"));
        Assert.Single(store.Load());
    }

    [Fact]
    public void DifferentAccounts_DoNotShareAQueue()
    {
        var storeA = Store("a@example.com");
        var storeB = Store("b@example.com");
        storeA.Add(Result(), DateTime.UtcNow.AddHours(1));

        Assert.Single(storeA.Load());
        Assert.Empty(storeB.Load());
    }

    [Fact]
    public void MultipleEntries_AllPersist()
    {
        var store = Store();
        store.Add(Result(subject: "First"), DateTime.UtcNow.AddHours(1));
        store.Add(Result(subject: "Second"), DateTime.UtcNow.AddHours(2));

        Assert.Equal(2, store.Load().Count);
    }
}
