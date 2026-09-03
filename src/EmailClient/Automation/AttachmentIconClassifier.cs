namespace EmailClient.Automation;

/// <summary>A file-type badge — the color and vector mark both the compose chip row (WPF) and the
/// reading-pane attachment chips (HTML) render from, so a PDF looks the same everywhere. Matched
/// by extension since that's all a filename gives; falls back to a generic file mark for anything
/// not worth a dedicated one.
/// <para><see cref="PathData"/> is a 16x16-viewBox path string, deliberately written using only
/// M/L/Z commands (no arcs) so the exact same string parses correctly both as an SVG "d" attribute
/// and as a WPF Geometry (Path.Data) — one definition, two renderers, no drift between them. Every
/// mark is drawn stroke-only (no fill) for the same reason: a single Fill+Stroke treatment applies
/// uniformly to all nine without a per-icon "is this one solid or outline" special case.</para></summary>
public readonly record struct AttachmentIconInfo(string ColorHex, string PathData);

public static class AttachmentIconClassifier
{
    public static AttachmentIconInfo For(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "svg" or "heic" or "tif" or "tiff"
                => new AttachmentIconInfo("#6D28D9", Marks.Image),   // app's own accent purple
            "pdf" => new AttachmentIconInfo("#C0392B", Marks.Pdf),
            "doc" or "docx" or "rtf" or "odt" => new AttachmentIconInfo("#3457A6", Marks.Doc),
            "xls" or "xlsx" or "csv" or "ods" => new AttachmentIconInfo("#2F8F5B", Marks.Chart),
            "ppt" or "pptx" or "odp" => new AttachmentIconInfo("#C56A1F", Marks.Slide),
            "zip" or "rar" or "7z" or "tar" or "gz" => new AttachmentIconInfo("#6B7280", Marks.Zip),
            "mp3" or "wav" or "m4a" or "flac" or "aac" => new AttachmentIconInfo("#9D3FB0", Marks.Audio),
            "mp4" or "mov" or "avi" or "mkv" or "webm" => new AttachmentIconInfo("#B23A6B", Marks.Video),
            "txt" or "log" or "md" => new AttachmentIconInfo("#6B7280", Marks.Doc),
            _ => new AttachmentIconInfo("#8B8794", Marks.Generic),
        };
    }

    private static class Marks
    {
        public const string Image = "M2,12 L5.6,7.2 L8,9.6 L10.8,5.6 L14,12 Z M3,3.4 L4.4,3.4 L4.4,4.8 L3,4.8 Z";
        public const string Pdf = "M3.5,2.2 L11.5,2.2 L11.5,13.4 L3.5,13.4 Z M5.2,8.4 L9.8,8.4 M5.2,10.4 L8.6,10.4";
        public const string Doc = "M3.6,4.6 L12.2,4.6 M3.6,7.6 L12.2,7.6 M3.6,10.6 L9,10.6";
        public const string Chart = "M3.4,10 L5,10 L5,12.4 L3.4,12.4 Z M6.6,7.2 L8.2,7.2 L8.2,12.4 L6.6,12.4 Z M9.8,3.6 L11.4,3.6 L11.4,12.4 L9.8,12.4 Z";
        public const string Slide = "M2.4,3.4 L13.6,3.4 L13.6,10.6 L2.4,10.6 Z M6.6,5.4 L10,7 L6.6,8.6 Z";
        public const string Zip = "M7,1.6 L8.6,1.6 L8.6,3.2 L7,3.2 L7,4.8 L8.6,4.8 L8.6,6.4 L7,6.4 L7,8 M6.2,8 L9.4,8 L9.4,10.8 L6.2,10.8 Z";
        public const string Audio = "M3.6,10.4 L5.6,10.4 L5.6,12.4 L3.6,12.4 Z M9.4,9 L11.4,9 L11.4,11 L9.4,11 Z M5.6,10.6 L5.6,3.6 L10.4,2.4 L10.4,9.2";
        public const string Video = "M2.4,4 L10.4,4 L10.4,11.4 L2.4,11.4 Z M11.2,6.2 L13.8,7.7 L11.2,9.2 Z";
        public const string Generic = "M3.6,2.2 L9.4,2.2 L11.6,4.4 L11.6,13.2 L3.6,13.2 Z M9.4,2.2 L9.4,4.4 L11.6,4.4";
    }
}
