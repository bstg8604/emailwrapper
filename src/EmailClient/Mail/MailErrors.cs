using System.Net.Sockets;
using System.Security.Authentication;

namespace EmailClient.Mail;

/// <summary>
/// Turns the exception types a failed IMAP/SMTP operation actually throws into a sentence a
/// non-technical user can act on, instead of the raw MailKit/.NET exception message (e.g.
/// "Authentication failed." with no indication of what to actually try) surfacing verbatim.
/// </summary>
public static class MailErrors
{
    public static string Friendly(Exception ex) => ex switch
    {
        MailKit.Security.AuthenticationException =>
            "Your email or password was rejected by the server. Check them in Account settings and try again.",
        AuthenticationException =>
            "Couldn't establish a secure connection to the server (a certificate or TLS problem). Check the server settings in Account settings.",
        SocketException =>
            "Couldn't reach the mail server — check your internet connection and the server address in Account settings.",
        TimeoutException or OperationCanceledException =>
            "The mail server took too long to respond. Check your connection and try again.",
        SessionExpiredException =>
            "You've been signed out — sign in again to continue.",
        _ => ex.Message,
    };
}
