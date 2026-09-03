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
                    "Your email or password was rejected by the server. Check them in Account settings and try again.",
                AuthenticationException =>
                    "Couldn't establish a secure connection to the server (a certificate/TLS problem, or no internet connection to verify it). Check your connection, or the server settings in Account settings.",
                SocketException =>
                    "Couldn't reach the mail server — check your internet connection and the server address in Account settings.",
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
}
