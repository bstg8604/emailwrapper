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
    public static string Friendly(Exception ex)
    {
        // .NET's SslStream doesn't always throw AuthenticationException directly — a TLS handshake
        // failure at the socket/IO layer (e.g. no internet to reach the CA's revocation server, so
        // the certificate can't be validated) is often wrapped in an IOException instead, with the
        // AuthenticationException one level down as InnerException. Matching only the outer
        // exception's type missed that case entirely and fell through to the raw-message fallback
        // below — which, for that specific error, is several paragraphs of bullet points dumped
        // verbatim into what's supposed to be a one-line status message. Walking the whole chain
        // catches it (and any other known type) regardless of how deep .NET wrapped it.
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var message = current switch
            {
                MailKit.Security.AuthenticationException =>
                    "Your email or password was rejected by the server. Check them in Settings and try again.",
                AuthenticationException =>
                    "Couldn't establish a secure connection to the server (a certificate/TLS problem, or no internet connection to verify it). Check your connection, or the server settings in Settings.",
                SocketException =>
                    "Couldn't reach the mail server — check your internet connection and the server address in Settings.",
                TimeoutException or OperationCanceledException =>
                    "The mail server took too long to respond. Check your connection and try again.",
                SessionExpiredException =>
                    "You've been signed out — sign in again to continue.",
                _ => null,
            };
            if (message is not null)
                return message;
        }

        // Nothing recognized anywhere in the chain — still never worth risking a raw multi-line
        // exception message (the TLS one above isn't the only exception type that can ramble) in a
        // spot meant to hold one line, so only the first line of whatever .NET actually said is used.
        var newline = ex.Message.IndexOfAny(['\r', '\n']);
        return newline < 0 ? ex.Message : ex.Message[..newline].TrimEnd();
    }

    /// <summary>True only for a genuine "wrong email/password" rejection from the server —
    /// MailKit.Security.AuthenticationException specifically, same chain-walk as Friendly's own
    /// switch above (for the same reason: it can arrive wrapped). Every *other* connect failure —
    /// no network, a TLS/certificate problem, a timeout — means the account itself is still fine
    /// and just isn't reachable right now, which is the distinction MainWindow's own
    /// auto-reconnect loop needs: only a real bad-credentials failure should stop it and ask the
    /// user to fix something, since retrying a wrong password forever would just be noise (and, on
    /// some servers, risk a lockout) — but retrying "no internet right now" is exactly the right
    /// thing to keep doing quietly.</summary>
    public static bool IsBadCredentials(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
            if (current is MailKit.Security.AuthenticationException)
                return true;
        return false;
    }
}
