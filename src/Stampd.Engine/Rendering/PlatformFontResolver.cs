using PdfSharp.Fonts;

namespace Stampd.Engine.Rendering;

/// <summary>
/// A minimal cross-platform <see cref="IFontResolver"/> for PDFsharp 6.x. Probes well-known
/// OS font locations on macOS, Linux, and Windows and serves the first available font for
/// every requested family.
/// </summary>
/// <remarks>
/// PDFsharp 6.x intentionally has no default font resolution outside Windows. Consumers
/// are expected to bring their own resolver. For the engine spike, a single OS-installed
/// font (Arial on macOS/Windows, DejaVu on Linux) covers every <c>XFont</c> the engine
/// constructs. Bold/italic styling is not faithfully rendered — every face maps to the same
/// regular file — but that is acceptable for proving the signing pipeline.
/// </remarks>
public sealed class PlatformFontResolver : IFontResolver
{
    private static readonly string[] CandidatePaths = BuildCandidatePaths();

    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<string, byte[]> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static string? _resolvedFacePath;

    /// <summary>
    /// Registers this resolver as the global PDFsharp font resolver. Idempotent — calling
    /// twice is harmless.
    /// </summary>
    public static void Register()
    {
        if (GlobalFontSettings.FontResolver is PlatformFontResolver)
        {
            return;
        }

        GlobalFontSettings.FontResolver = new PlatformFontResolver();
    }

    /// <inheritdoc />
    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var path = ResolveFacePath();
        if (path is null)
        {
            return null;
        }

        // Returning the file path as the face name lets GetFont look the file up directly.
        // Bold/italic are simulated by PDFsharp since we don't supply distinct face files.
        return new FontResolverInfo(path, mustSimulateBold: bold, mustSimulateItalic: italic);
    }

    /// <inheritdoc />
    public byte[]? GetFont(string faceName)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(faceName, out var cached))
            {
                return cached;
            }

            if (!File.Exists(faceName))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(faceName);
            Cache[faceName] = bytes;
            return bytes;
        }
    }

    private static string? ResolveFacePath()
    {
        if (_resolvedFacePath is not null)
        {
            return _resolvedFacePath;
        }

        foreach (var candidate in CandidatePaths)
        {
            if (File.Exists(candidate))
            {
                _resolvedFacePath = candidate;
                return _resolvedFacePath;
            }
        }

        return null;
    }

    private static string[] BuildCandidatePaths()
    {
        if (OperatingSystem.IsMacOS())
        {
            return
            [
                "/System/Library/Fonts/Supplemental/Arial.ttf",
                "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",
                "/System/Library/Fonts/Supplemental/Verdana.ttf",
                "/Library/Fonts/Arial.ttf",
            ];
        }

        if (OperatingSystem.IsLinux())
        {
            return
            [
                "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                "/usr/share/fonts/TTF/DejaVuSans.ttf",
                "/usr/share/fonts/dejavu/DejaVuSans.ttf",
                "/usr/share/fonts/liberation/LiberationSans-Regular.ttf",
                "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            ];
        }

        if (OperatingSystem.IsWindows())
        {
            var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            return
            [
                Path.Combine(fontsDir, "arial.ttf"),
                Path.Combine(fontsDir, "verdana.ttf"),
                Path.Combine(fontsDir, "calibri.ttf"),
            ];
        }

        return [];
    }
}
