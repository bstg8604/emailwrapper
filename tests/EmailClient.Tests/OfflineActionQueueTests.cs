using System.IO;
using EmailClient.Mail;
using Xunit;

namespace EmailClient.Tests;

public class OfflineActionQueueTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "purplemail-offline-actions-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private OfflineActionQueue Store(string email = "a@b.c") => new(email, _root);

    [Fact]
    public void Add_ThenLoad_RoundTrips()
    {
        var store = Store();
        var id = store.Add(OfflineActionKind.Delete, "msg-1");
        var loaded = store.Load();

        Assert.Single(loaded);
        Assert.Equal(id, loaded[0].Id);
        Assert.Equal(OfflineActionKind.Delete, loaded[0].Kind);
        Assert.Equal("msg-1", loaded[0].MessageId);
        Assert.Null(loaded[0].TargetFolder);
    }

    [Fact]
    public void Add_WithMessageIdHeader_Persists()
    {
        var store = Store();
        store.Add(OfflineActionKind.Delete, "msg-1", messageIdHeader: "<abc123@example.com>");

        var loaded = store.Load();
        Assert.Equal("<abc123@example.com>", loaded[0].MessageIdHeader);
    }

    [Fact]
    public void Add_WithoutMessageIdHeader_DefaultsToNull()
    {
        var store = Store();
        store.Add(OfflineActionKind.Delete, "msg-1");

        Assert.Null(store.Load()[0].MessageIdHeader);
    }

    [Fact]
    public void Add_Move_PersistsTargetFolder()
    {
        var store = Store();
        store.Add(OfflineActionKind.Move, "msg-1", "Archive");

        var loaded = store.Load();
        Assert.Equal("Archive", loaded[0].TargetFolder);
    }

    [Fact]
    public void Remove_ExistingId_ReturnsTrue_AndDropsIt()
    {
        var store = Store();
        var id = store.Add(OfflineActionKind.Archive, "msg-1");

        Assert.True(store.Remove(id));
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Remove_UnknownId_ReturnsFalse_AndLeavesOthersAlone()
    {
        var store = Store();
        store.Add(OfflineActionKind.MarkRead, "msg-1");

        Assert.False(store.Remove("does-not-exist"));
        Assert.Single(store.Load());
    }

    [Fact]
    public void DifferentAccounts_DoNotShareAQueue()
    {
        var storeA = Store("a@example.com");
        var storeB = Store("b@example.com");
        storeA.Add(OfflineActionKind.Delete, "msg-1");

        Assert.Single(storeA.Load());
        Assert.Empty(storeB.Load());
    }

    [Fact]
    public void MultipleActions_PersistInOrder()
    {
        var store = Store();
        store.Add(OfflineActionKind.Delete, "msg-1");
        store.Add(OfflineActionKind.MarkUnread, "msg-2");

        var loaded = store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("msg-1", loaded[0].MessageId);
        Assert.Equal("msg-2", loaded[1].MessageId);
    }
}
