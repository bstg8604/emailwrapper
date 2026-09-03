using EmailClient.Automation;
using Xunit;

namespace EmailClient.Tests;

public class SearchQueryParserTests
{
    [Fact]
    public void PlainQuery_HasNoOperators_AndIsAllFreeText()
    {
        var parsed = SearchQueryParser.Parse("budget report");
        Assert.False(parsed.HasOperators);
        Assert.Equal("budget report", parsed.FreeText);
    }

    [Fact]
    public void From_IsExtracted_AndRemovedFromFreeText()
    {
        var parsed = SearchQueryParser.Parse("from:jayesh budget");
        Assert.Equal("jayesh", parsed.From);
        Assert.Equal("budget", parsed.FreeText);
    }

    [Fact]
    public void To_And_Subject_AreExtracted()
    {
        var parsed = SearchQueryParser.Parse("to:shiva subject:award");
        Assert.Equal("shiva", parsed.To);
        Assert.Equal("award", parsed.Subject);
        Assert.Equal("", parsed.FreeText);
    }

    [Theory]
    [InlineData("has:attachment", true)]
    [InlineData("has:ATTACHMENT", true)]
    public void HasAttachment_IsRecognizedCaseInsensitively(string token, bool expected)
    {
        Assert.Equal(expected, SearchQueryParser.Parse(token).HasAttachment);
    }

    [Fact]
    public void IsUnread_SetsUnreadTrue()
    {
        Assert.True(SearchQueryParser.Parse("is:unread").Unread);
    }

    [Fact]
    public void IsRead_SetsUnreadFalse()
    {
        Assert.False(SearchQueryParser.Parse("is:read").Unread);
    }

    [Theory]
    [InlineData("is:starred")]
    [InlineData("is:flagged")]
    public void IsStarredOrFlagged_SetsStarredTrue(string token)
    {
        Assert.True(SearchQueryParser.Parse(token).Starred);
    }

    [Fact]
    public void UnrecognizedKeyValueShape_IsKeptAsFreeText()
    {
        // A stray colon in ordinary text (a time, say) isn't operator syntax — it should still be
        // searched for, not silently dropped.
        var parsed = SearchQueryParser.Parse("meeting at 10:30");
        Assert.False(parsed.HasOperators);
        Assert.Equal("meeting at 10:30", parsed.FreeText);
    }

    [Fact]
    public void CombinesMultipleOperatorsWithFreeText()
    {
        var parsed = SearchQueryParser.Parse("from:jay has:attachment is:unread award ceremony");
        Assert.Equal("jay", parsed.From);
        Assert.True(parsed.HasAttachment);
        Assert.True(parsed.Unread);
        Assert.Equal("award ceremony", parsed.FreeText);
        Assert.True(parsed.HasOperators);
    }

    [Fact]
    public void EmptyQuery_ProducesEmptyFreeTextAndNoOperators()
    {
        var parsed = SearchQueryParser.Parse("");
        Assert.False(parsed.HasOperators);
        Assert.Equal("", parsed.FreeText);
    }

    [Fact]
    public void QuotedPhrase_StaysOneFreeTextToken()
    {
        var parsed = SearchQueryParser.Parse("\"budget report\" urgent");
        Assert.Equal("budget report urgent", parsed.FreeText);
    }

    [Fact]
    public void QuotedOperatorValue_StaysIntact()
    {
        var parsed = SearchQueryParser.Parse("subject:\"trip report\"");
        Assert.Equal("trip report", parsed.Subject);
        Assert.Equal("", parsed.FreeText);
    }

    [Theory]
    [InlineData("older_than:7d")]
    [InlineData("older_than:2m")]
    [InlineData("older_than:1y")]
    public void OlderThan_IsRecognized(string token)
    {
        var parsed = SearchQueryParser.Parse(token);
        Assert.NotNull(parsed.OlderThan);
        Assert.True(parsed.HasOperators);
    }

    [Fact]
    public void NewerThan_IsRecognized()
    {
        var parsed = SearchQueryParser.Parse("newer_than:3d");
        Assert.NotNull(parsed.NewerThan);
        Assert.True(parsed.NewerThan < DateTime.UtcNow);
    }

    [Theory]
    [InlineData("larger:5M", 5L * 1024 * 1024)]
    [InlineData("larger:100K", 100L * 1024)]
    [InlineData("larger:200", 200L)]
    public void Larger_ParsesSizeSuffixes(string token, long expectedBytes)
    {
        Assert.Equal(expectedBytes, SearchQueryParser.Parse(token).LargerThanBytes);
    }

    [Fact]
    public void Smaller_IsRecognized()
    {
        Assert.Equal(1024L * 1024, SearchQueryParser.Parse("smaller:1M").SmallerThanBytes);
    }

    [Fact]
    public void InvalidRelativeAge_FallsBackToFreeText()
    {
        var parsed = SearchQueryParser.Parse("older_than:abc");
        Assert.Null(parsed.OlderThan);
        Assert.Equal("older_than:abc", parsed.FreeText);
    }
}
