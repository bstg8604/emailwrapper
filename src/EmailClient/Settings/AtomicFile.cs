using System.IO;

namespace EmailClient.Settings;

/// <summary>
/// Write-to-temp-then-rename, instead of overwriting the real file directly — a crash or power
/// loss mid-write to the real path leaves a half-written, corrupt file that <c>Load()</c> then
/// silently treats as "nothing saved" (settings reset / forced sign-out with no error shown). An
/// NTFS rename onto an existing file is atomic, so the real file is always either the old
/// complete version or the new complete version, never a partial one.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteAllText(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
