using EmailClient.Automation;
using Xunit;

namespace EmailClient.Tests;

public class MailTextTests
{
    [Fact]
    public void HtmlToPlainText_StripsScriptAndStyle()
    {
        var html = "<style>body{color:red}</style><p>Hello</p><script>alert(1)</script>";
        Assert.Equal("Hello", MailText.HtmlToPlainText(html));
    }

    [Fact]
    public void HtmlToPlainText_ConvertsListItemsToBullets()
    {
        var html = "<ul><li>First</li><li>Second</li></ul>";
        var text = MailText.HtmlToPlainText(html);
        Assert.Equal("• First\n• Second", text);
    }

    [Fact]
    public void HtmlToPlainText_DecodesEntities()
    {
        Assert.Equal("Tom & Jerry", MailText.HtmlToPlainText("<p>Tom &amp; Jerry</p>"));
    }

    [Fact]
    public void HtmlToPlainText_CollapsesRepeatedBlankLines()
    {
        var html = "<p>One</p><p></p><p></p><p></p><p>Two</p>";
        var text = MailText.HtmlToPlainText(html);
        Assert.DoesNotContain("\n\n\n", text);
    }

    [Fact]
    public void HtmlToPlainText_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", MailText.HtmlToPlainText(""));
        Assert.Equal("", MailText.HtmlToPlainText("   "));
    }

    [Fact]
    public void Snippet_TruncatesLongTextWithEllipsis()
    {
        var html = "<p>" + new string('a', 200) + "</p>";
        var snippet = MailText.Snippet(html, maxLength: 20);
        Assert.Equal(21, snippet.Length); // 20 chars + the ellipsis character
        Assert.EndsWith("…", snippet);
    }

    [Fact]
    public void Snippet_ShortText_ReturnedAsIs()
    {
        Assert.Equal("Hello world", MailText.Snippet("<p>Hello world</p>"));
    }

    [Fact]
    public void Snippet_NeverSplitsASurrogatePairAtTheCut()
    {
        // An astral-plane character (outside the BMP, e.g. this emoji) is two UTF-16 chars —
        // a raw-index cut landing right between them would leave an orphaned high surrogate
        // at the end, rendering as a broken glyph instead of the ellipsis reading cleanly.
        var emoji = "\U0001F600";
        var html = "<p>" + new string('a', 19) + emoji + "</p>";
        var snippet = MailText.Snippet(html, maxLength: 20);

        Assert.EndsWith("\u2026", snippet);
        Assert.False(char.IsSurrogate(snippet[^2]), "no lone surrogate half should sit before the ellipsis");
    }

    [Fact]
    public void HtmlToPlainText_QuotedAttributeContainingGreaterThan_DoesNotLeakTagTail()
    {
        var html = """<p>Before</p><img alt="1 > 2" src="x.png"><p>After</p>""";
        var text = MailText.HtmlToPlainText(html);

        Assert.DoesNotContain("src=", text);
        Assert.DoesNotContain("png", text);
    }

    [Fact]
    public void HtmlToPlainText_CollapsesRepeatedNonBreakingSpaces()
    {
        var html = "<p>Indented:&nbsp;&nbsp;&nbsp;text</p>";
        var text = MailText.HtmlToPlainText(html);

        Assert.Equal("Indented: text", text);
    }

    [Fact]
    public void Quote_PrefixesEveryLineIncludingBlankOnes()
    {
        var quoted = MailText.Quote("Line one\n\nLine two");
        Assert.Equal("> Line one\n>\n> Line two", quoted);
    }

    [Theory]
    [InlineData("Academic Office <academic@iitb.ac.in>", "Academic Office")]
    [InlineData("plain@iitb.ac.in", "plain")]
    [InlineData("", "")]
    public void DisplayName_ExtractsFriendlyName(string address, string expected) =>
        Assert.Equal(expected, MailText.DisplayName(address));

    [Theory]
    [InlineData("Academic Office <academic@iitb.ac.in>", "academic@iitb.ac.in")]
    [InlineData("plain@iitb.ac.in", "plain@iitb.ac.in")]
    public void AddressOnly_ExtractsBareAddress(string address, string expected) =>
        Assert.Equal(expected, MailText.AddressOnly(address));

    [Fact]
    public void MergeRecipients_DropsDuplicatesAndExcluded()
    {
        var merged = MailText.MergeRecipients(
            exclude: ["me@iitb.ac.in"],
            "Alice <alice@iitb.ac.in>, me@iitb.ac.in",
            "Alice <alice@iitb.ac.in>, Bob <bob@iitb.ac.in>");

        Assert.Equal("Alice <alice@iitb.ac.in>, Bob <bob@iitb.ac.in>", merged);
    }

    [Fact]
    public void MergeRecipients_IsCaseInsensitiveForExclusionAndDedupe()
    {
        var merged = MailText.MergeRecipients(
            exclude: ["ME@IITB.AC.IN"],
            "me@iitb.ac.in, Alice <ALICE@iitb.ac.in>, alice@iitb.ac.in");

        Assert.Equal("Alice <ALICE@iitb.ac.in>", merged);
    }

    [Fact]
    public void FormatListDate_Today_ReturnsTimeOnly()
    {
        var now = DateTime.Now;
        var formatted = MailText.FormatListDate(now);
        Assert.DoesNotContain(now.Year.ToString(), formatted);
    }

    [Fact]
    public void FormatListDate_Yesterday_ReturnsYesterday() =>
        Assert.Equal("Yesterday", MailText.FormatListDate(DateTime.Today.AddDays(-1)));
}
