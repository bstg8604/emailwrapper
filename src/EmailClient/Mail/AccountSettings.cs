using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailClient.Settings;

namespace EmailClient.Mail;

public enum SmtpSecurity { StartTls, SslOnConnect }

/// <summary>
/// One saved account: IMAP/SMTP server details plus credentials. Persisted (via <see cref="AccountStore"/>)
/// encrypted with Windows DPAPI (CurrentUser scope) rather than plain JSON — this file otherwise
/// sits right next to a password in <c>%LOCALAPPDATA%</c>.
/// </summary>
public sealed class AccountSettings
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string DisplayName { get; set; } = "";

    public string ImapHost { get; set; } = "imap.iitb.ac.in";
    public int ImapPort { get; set; } = 993;

    // Best-guess default from the SMTP submission host that resolves for IITB; unconfirmed for
    // this account's network — the sign-in dialog's Advanced section lets it be corrected without
    // a rebuild, and a failed send says so explicitly rather than failing silently.
    public string SmtpHost { get; set; } = "smtp-auth.iitb.ac.in";
    public int SmtpPort { get; set; } = 587;
    public SmtpSecurity SmtpSecurity { get; set; } = SmtpSecurity.StartTls;

    /// <summary>True if any server field has been changed from the plain-defaults sign-in
    /// starts with — used to decide whether the Advanced section should open pre-expanded.</summary>
    public bool UsesNonDefaultServers()
    {
        var defaults = new AccountSettings();
        return ImapHost != defaults.ImapHost || ImapPort != defaults.ImapPort
            || SmtpHost != defaults.SmtpHost || SmtpPort != defaults.SmtpPort
            || SmtpSecurity != defaults.SmtpSecurity;
    }

    public static bool Exists() => AccountStore.Load().Accounts.Count > 0;

    /// <summary>The active account, or null if none is signed in — the shape every existing call
    /// site (single-account era) expects.</summary>
    public static AccountSettings? Load()
    {
        var store = AccountStore.Load();
        return store.Accounts.FirstOrDefault(a =>
            a.Email.Equals(store.ActiveEmail, StringComparison.OrdinalIgnoreCase))
            ?? store.Accounts.FirstOrDefault();
    }

    /// <summary>Saves this account into the store (adding it, or updating the existing entry for
    /// the same email) and makes it the active one.</summary>
    public void Save()
    {
        var store = AccountStore.Load();
        store.Accounts.RemoveAll(a => a.Email.Equals(Email, StringComparison.OrdinalIgnoreCase));
        store.Accounts.Add(this);
        store.ActiveEmail = Email;
        store.Persist();
    }

    /// <summary>Removes this account from the store. If it was active, another saved account
    /// (if any) becomes active.</summary>
    public void Remove()
    {
        var store = AccountStore.Load();
        store.Accounts.RemoveAll(a => a.Email.Equals(Email, StringComparison.OrdinalIgnoreCase));
        if (store.ActiveEmail?.Equals(Email, StringComparison.OrdinalIgnoreCase) == true)
            store.ActiveEmail = store.Accounts.FirstOrDefault()?.Email;
        store.Persist();
    }

    /// <summary>Drops every saved account — the original "sign out" behaviour, kept for the case
    /// where there's only ever been the one account.</summary>
    public static void SignOut() => AccountStore.Clear();

    public string Identity => string.IsNullOrWhiteSpace(DisplayName) ? Email : $"{DisplayName} <{Email}>";

    /// <summary>Initial shown on the account avatar — display name if set, else the email's local part.</summary>
    public string Initial
    {
        get
        {
            var source = string.IsNullOrWhiteSpace(DisplayName) ? Email : DisplayName;
            var letter = source.Trim().FirstOrDefault(char.IsLetterOrDigit);
            return letter == default ? "?" : char.ToUpperInvariant(letter).ToString();
        }
    }
}

/// <summary>
/// Every saved account plus which one is active — lets "Switch account" pick between accounts
/// that are all still signed in, rather than discarding one to sign into another.
/// </summary>
public sealed class AccountStore
{
    public List<AccountSettings> Accounts { get; set; } = new();
    public string? ActiveEmail { get; set; }

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IITBWebmailWrapper");

    private static string AccountPath => Path.Combine(DataDir, "account.dat");

    public static AccountStore Load()
    {
        try
        {
            if (!File.Exists(AccountPath))
                return new AccountStore();

            var encrypted = File.ReadAllBytes(AccountPath);
            var json = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AccountStore>(Encoding.UTF8.GetString(json)) ?? new AccountStore();
        }
        catch (Exception)
        {
            // Corrupt file, or DPAPI can't unprotect it (different user/machine) — treat as
            // "no saved accounts" rather than crashing the app on startup.
            return new AccountStore();
        }
    }

    public void Persist()
    {
        Directory.CreateDirectory(DataDir);
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
        var encrypted = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        AtomicFile.WriteAllBytes(AccountPath, encrypted);
    }

    public static void Clear()
    {
        try { File.Delete(AccountPath); } catch (Exception) { /* nothing to clean up */ }
    }
}
