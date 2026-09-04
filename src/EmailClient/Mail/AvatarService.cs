using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DnsClient;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace EmailClient.Mail;

/// <summary>A sender's resolved avatar image, plus whether it came from a domain that also
/// published a Verified Mark Certificate (the badge Gmail shows next to it).</summary>
public sealed record AvatarResult(BitmapSource Image, bool Verified);

/// <summary>Looks up a sender domain's BIMI record — the open DNS standard behind Gmail's own
/// verified-logo badge, published at "default._bimi.&lt;domain&gt;" as a TXT record pointing to an
/// SVG logo (plus, optionally, a Verified Mark Certificate). Most domains don't publish one; those
/// return null and the caller falls back to its existing colored-initial avatar. This does not
/// validate the VMC's certificate chain the way Gmail's backend does — a domain simply having an
/// "a=" tag is treated as "show the badge," matching how other third-party BIMI-aware clients
/// behave, short of standing up a full CA trust store for it.</summary>
public static class AvatarService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly LookupClient Dns = new();

    // One outstanding/completed lookup per domain, shared across every row that needs it — a
    // 40-row inbox from the same sender should trigger one DNS query and one SVG fetch, not 40.
    private static readonly ConcurrentDictionary<string, Task<AvatarResult?>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    // Same app-data folder the rest of Purplemail's per-user state (settings, contacts, signatures)
    // already lives in — see build/Purplemail.iss's [UninstallDelete] comment for why it's never
    // touched by uninstall.
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IITBWebmailWrapper", "avatars");

    // Domains rarely rotate their BIMI logo; a week keeps repeat lookups fast without going stale
    // for long if one ever does.
    private static readonly TimeSpan DiskCacheTtl = TimeSpan.FromDays(7);

    public static Task<AvatarResult?> GetAvatarAsync(string emailAddress)
    {
        var domain = emailAddress?.Split('@').ElementAtOrDefault(1)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(domain))
            return Task.FromResult<AvatarResult?>(null);
        return _cache.GetOrAdd(domain, FetchAsync);
    }

    private static async Task<AvatarResult?> FetchAsync(string domain)
    {
        try
        {
            var pngPath = Path.Combine(CacheDir, domain + ".png");
            var metaPath = Path.Combine(CacheDir, domain + ".meta");
            if (File.Exists(pngPath) && File.Exists(metaPath)
                && DateTime.UtcNow - File.GetLastWriteTimeUtc(metaPath) < DiskCacheTtl)
            {
                var verifiedCached = (await File.ReadAllTextAsync(metaPath)).Trim() == "verified";
                return new AvatarResult(LoadPng(pngPath), verifiedCached);
            }

            // A cached "this domain has no BIMI record" marker avoids re-querying DNS for every
            // single message from a sender whose domain simply doesn't publish one — the common case.
            var negativePath = Path.Combine(CacheDir, domain + ".none");
            if (File.Exists(negativePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(negativePath) < DiskCacheTtl)
                return null;

            var dnsResult = await Dns.QueryAsync($"default._bimi.{domain}", QueryType.TXT);
            var txt = dnsResult.Answers.TxtRecords().FirstOrDefault()?.Text.FirstOrDefault();
            var logoUrl = txt is null ? null : ParseTag(txt, "l");
            if (string.IsNullOrEmpty(logoUrl))
                return MarkNegative(negativePath);

            var verified = !string.IsNullOrEmpty(ParseTag(txt!, "a"));
            var svgBytes = await Http.GetByteArrayAsync(logoUrl);
            var bitmap = RenderSvg(svgBytes);
            if (bitmap is null)
                return MarkNegative(negativePath);

            Directory.CreateDirectory(CacheDir);
            SavePng(pngPath, bitmap);
            await File.WriteAllTextAsync(metaPath, verified ? "verified" : "unverified");
            if (File.Exists(negativePath))
                File.Delete(negativePath);
            return new AvatarResult(bitmap, verified);
        }
        catch (Exception)
        {
            // Offline, DNS server unreachable, malformed record, unrenderable SVG — none of these
            // should block showing the message, just its avatar. Silent fallback to initials.
            return null;
        }
    }

    private static AvatarResult? MarkNegative(string negativePath)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(negativePath, "");
        }
        catch (Exception) { /* best-effort cache; a miss just means we ask DNS again next time */ }
        return null;
    }

    private static string? ParseTag(string txt, string tag)
    {
        // "v=BIMI1; l=https://...; a=https://...pem" — split on ';', then the first '=' in each part.
        foreach (var part in txt.Split(';'))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals(tag, StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }
        return null;
    }

    private static BitmapSource? RenderSvg(byte[] svgBytes)
    {
        // Rendered once at a fixed resolution higher than any on-screen avatar size, so the same
        // cached bitmap stays crisp at both the message-list and reading-pane sizes and on
        // high-DPI displays, without re-rendering the vector per display size.
        const int size = 96;
        using var stream = new MemoryStream(svgBytes);
        var settings = new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true };
        var reader = new FileSvgReader(settings);
        var drawing = reader.Read(stream);
        if (drawing is null)
            return null;

        var bounds = drawing.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var scale = size / Math.Max(bounds.Width, bounds.Height);
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            dc.DrawDrawing(drawing);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static void SavePng(string path, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static BitmapImage LoadPng(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        // Loads the bytes into memory up front and releases the file handle immediately — without
        // this the file stays locked open for the image's lifetime, which would break re-fetching
        // the same domain's avatar (overwriting pngPath) later in the session.
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
