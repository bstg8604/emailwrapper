namespace EmailClient.Mail;

/// <summary>
/// How well the app is currently talking to the mail server, for the status-bar indicator.
///
/// Worth distinguishing <see cref="Push"/> from <see cref="Polling"/> because the difference is
/// user-visible: on push, mail is announced within a second or two; on polling it can take a
/// minute. IMAP IDLE can be abandoned permanently mid-session (see the breaker in
/// <see cref="ImapMailBackend"/>), and until this existed that happened completely silently.
/// </summary>
public enum ConnectionState
{
    /// <summary>No account connected — sample data.</summary>
    SignedOut,

    Connecting,

    /// <summary>Connected with IMAP IDLE active: new mail arrives as a push, near-instantly.</summary>
    Push,

    /// <summary>Connected, but IDLE is unavailable — new mail is found by the 60-second poll.</summary>
    Polling,

    /// <summary>Lost the server; the next poll will try to re-establish it.</summary>
    Offline,
}

public static class ConnectionStateText
{
    /// <summary>Short label for the status-bar indicator.</summary>
    public static string Label(this ConnectionState state) => state switch
    {
        ConnectionState.SignedOut => "Not signed in",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Push => "Connected",
        ConnectionState.Polling => "Connected (checking every minute)",
        ConnectionState.Offline => "Offline — reconnecting…",
        _ => "",
    };

    /// <summary>The longer explanation, shown on hover.</summary>
    public static string Detail(this ConnectionState state) => state switch
    {
        ConnectionState.SignedOut => "Sign in to connect to your mailbox.",
        ConnectionState.Connecting => "Signing in to the mail server.",
        ConnectionState.Push => "New mail is pushed by the server and announced as it arrives.",
        ConnectionState.Polling =>
            "The server isn't allowing a push connection, so Purplemail checks for new mail once a minute. "
            + "New mail can take up to a minute to appear.",
        ConnectionState.Offline =>
            "Can't reach the mail server right now. Purplemail keeps retrying — new mail won't be announced until it reconnects.",
        _ => "",
    };
}
