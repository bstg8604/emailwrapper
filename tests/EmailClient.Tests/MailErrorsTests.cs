using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using EmailClient.Mail;
using Xunit;

namespace EmailClient.Tests;

public class MailErrorsTests
{
    [Fact]
    public void RecognizesKnownExceptionType_AtTheTopLevel()
    {
        var message = MailErrors.Friendly(new SocketException());
        Assert.Contains("internet connection", message);
    }

    [Fact]
    public void RecognizesKnownExceptionType_WrappedInsideAnUnrelatedOuterException()
    {
        // .NET's SslStream doesn't always throw AuthenticationException directly — a handshake
        // failure at the socket/IO layer wraps it in an IOException instead. The real live bug this
        // guards: that wrapped shape used to fall through to the raw-message fallback entirely.
        var wrapped = new IOException("The handshake failed.", new AuthenticationException(
            "The server's SSL certificate could not be validated for the following reasons:\r\n"
            + "    • The revocation function was unable to check revocation for the certificate."));

        var message = MailErrors.Friendly(wrapped);

        Assert.DoesNotContain("revocation", message);
        Assert.Contains("secure connection", message);
    }

    [Fact]
    public void UnrecognizedException_FallsBackToOnlyTheFirstLine()
    {
        var ex = new InvalidOperationException(
            "First line of the message.\r\nSecond line nobody asked to see.\r\nThird line either.");

        var message = MailErrors.Friendly(ex);

        Assert.Equal("First line of the message.", message);
    }

    [Fact]
    public void UnrecognizedException_SingleLineMessage_PassesThroughUnchanged()
    {
        var message = MailErrors.Friendly(new InvalidOperationException("Just one line."));
        Assert.Equal("Just one line.", message);
    }
}
