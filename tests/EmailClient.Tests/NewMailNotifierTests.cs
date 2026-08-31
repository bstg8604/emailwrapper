using EmailClient.Automation;
using EmailClient.Mail;
using Xunit;

namespace EmailClient.Tests;

public class NewMailNotifierTests
{
    private static InboxRow Row(string id, bool unread = true, string sender = "Ada", string subject = "Hi") =>
        new(id, sender, subject, "", "9:00 AM", unread);

    [Fact]
    public void FirstPage_IsBaseline_AndAnnouncesNothing()
    {
        var notifier = new NewMailNotifier();
        Assert.Empty(notifier.Collect([Row("1"), Row("2")]));
    }

    [Fact]
    public void AnnouncesEveryNewUnreadRow_NotJustTheTopOne()
    {
        var notifier = new NewMailNotifier();
        notifier.Collect([Row("1")]);

        var fresh = notifier.Collect([Row("4"), Row("3"), Row("2"), Row("1")]);

        Assert.Equal(["4", "3", "2"], fresh.Select(r => r.Id));
    }

    [Fact]
    public void AlreadyReadArrivals_AreNotAnnounced()
    {
        var notifier = new NewMailNotifier();
        notifier.Collect([Row("1")]);

        Assert.Empty(notifier.Collect([Row("2", unread: false), Row("1")]));
    }

    [Fact]
    public void SameMessageIsAnnouncedOnlyOnce()
    {
        var notifier = new NewMailNotifier();
        notifier.Collect([Row("1")]);

        Assert.Single(notifier.Collect([Row("2"), Row("1")]));
        Assert.Empty(notifier.Collect([Row("2"), Row("1")]));
    }

    [Fact]
    public void DeletingTheTopMessage_DoesNotAnnounceTheOneBelowIt()
    {
        // The old "top row id changed and it's unread" test fired here even though no mail arrived.
        var notifier = new NewMailNotifier();
        notifier.Collect([Row("2"), Row("1")]);

        Assert.Empty(notifier.Collect([Row("1")]));
    }

    [Fact]
    public void MessageReturningToThePage_IsNotAnnouncedAgain()
    {
        var notifier = new NewMailNotifier();
        notifier.Collect([Row("2"), Row("1")]);
        notifier.Collect([Row("2")]);

        Assert.Empty(notifier.Collect([Row("2"), Row("1")]));
    }

    [Fact]
    public void Reset_RebaselinesSilently()
    {
        var notifier = new NewMailNotifier();
        notifier.Collect([Row("1")]);
        notifier.Reset();

        Assert.Empty(notifier.Collect([Row("9"), Row("8")]));
    }

    [Fact]
    public void IsPrimed_FalseUntilTheFirstCollect()
    {
        var notifier = new NewMailNotifier();
        Assert.False(notifier.IsPrimed);

        notifier.Collect([Row("1")]);

        Assert.True(notifier.IsPrimed);
    }

    [Fact]
    public void IsPrimed_FalseAgainAfterReset()
    {
        var notifier = new NewMailNotifier();
        notifier.Collect([Row("1")]);
        notifier.Reset();

        Assert.False(notifier.IsPrimed);
    }

    [Fact]
    public void Describe_SingleMessage_ReadsLikeTheMessage()
    {
        var (title, text) = NewMailNotifier.Describe([Row("1", sender: "Ada", subject: "Lunch?")]);

        Assert.Equal("New mail", title);
        Assert.Equal("Ada: Lunch?", text);
    }

    [Fact]
    public void Describe_Burst_LeadsWithTheCountAndSummarisesTheRest()
    {
        var rows = Enumerable.Range(1, 5).Select(i => Row(i.ToString(), subject: $"S{i}")).ToList();

        var (title, text) = NewMailNotifier.Describe(rows);

        Assert.Equal("5 new messages", title);
        Assert.Contains("Ada: S1", text);
        Assert.Contains("Ada: S3", text);
        Assert.DoesNotContain("Ada: S4", text);
        Assert.Contains("and 2 more", text);
    }

    [Fact]
    public void Describe_FillsInBlankSenderAndSubject()
    {
        var (_, text) = NewMailNotifier.Describe([Row("1", sender: "  ", subject: "")]);

        Assert.Equal("(unknown sender): (no subject)", text);
    }

    [Fact]
    public void Describe_TruncatesAnOverlongLine()
    {
        var (_, text) = NewMailNotifier.Describe([Row("1", subject: new string('x', 300))]);

        Assert.True(text.Length <= 90);
        Assert.EndsWith("…", text);
    }
}
