using System.IO;
using System.Text;

namespace EmailClient.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// A rolling text log next to the app's settings, so a failure that happened yesterday on the
/// user's machine can still be examined today.
///
/// The app previously had none: 100-plus catch blocks either swallowed the exception or replaced
/// the status-bar text with a sentence the next refresh overwrote seconds later. A user reporting
/// "it stopped telling me about mail" left literally nothing to look at — the IMAP IDLE loop, for
/// one, can abandon push permanently after three failures without a trace.
///
/// Deliberately hand-rolled rather than a logging package: the app takes four NuGet dependencies
/// total, and this needs to do exactly two things — append a line, and not grow forever.
/// </summary>
public static class Log
{
    /// <summary>Rolls at 1 MB, keeping one previous file. Enough history to cover a session or
    /// two of failures without ever becoming a disk-space question.</summary>
    private const long MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static bool _failed;

    public static string Directory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IITBWebmailWrapper");

    public static string FilePath => Path.Combine(Directory, "purplemail.log");

    private static string PreviousPath => Path.Combine(Directory, "purplemail.prev.log");

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);

    public static void Info(string message) => Write(LogLevel.Info, message, null);

    public static void Warn(string message, Exception? ex = null) => Write(LogLevel.Warn, message, ex);

    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    public static void Write(LogLevel level, string message, Exception? ex)
    {
        // Logging must never be the reason something fails. Once writing has failed once (a
        // read-only profile, a locked file), stop trying rather than paying the cost on every call.
        if (_failed)
            return;

        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
                .Append(message);

            if (ex is not null)
                line.AppendLine().Append("    ").Append(Describe(ex).Replace("\n", "\n    "));

            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                Roll();
                File.AppendAllText(FilePath, line.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            _failed = true;
        }
    }

    /// <summary>Full type, message and stack for every exception in the chain — an
    /// <c>ex.Message</c> alone routinely reads "One or more errors occurred", which says nothing.</summary>
    public static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            sb.Append(current.GetType().FullName).Append(": ").AppendLine(current.Message);
            if (current.StackTrace is { } stack)
                sb.AppendLine(stack);
            if (current.InnerException is not null)
                sb.AppendLine("--- caused by ---");
        }
        return sb.ToString().TrimEnd();
    }

    private static void Roll()
    {
        try
        {
            var file = new FileInfo(FilePath);
            if (!file.Exists || file.Length < MaxBytes)
                return;

            // One generation back is kept; the older one is replaced rather than accumulating.
            File.Move(FilePath, PreviousPath, overwrite: true);
        }
        catch (Exception)
        {
            // A failed roll is not a reason to lose the line being written.
        }
    }

    /// <summary>Written once at startup so every log opens with what build and OS produced it.</summary>
    public static void WriteHeader()
    {
        var version = typeof(Log).Assembly.GetName().Version?.ToString() ?? "unknown";
        Info($"--- Purplemail {version} starting | Windows {Environment.OSVersion.Version} | .NET {Environment.Version} ---");
    }
}
