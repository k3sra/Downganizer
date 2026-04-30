using System.Text;
using Downganizer.Configuration;

namespace Downganizer.Services;

/// <summary>
/// Pure routing logic. Given a source path, decides where it belongs.
/// No I/O, no file system mutation - just string manipulation, easy to reason about.
///
/// Three rules:
///   1. Directory  -> Packages\YYYY\MM\DD\[FolderName]
///   2. File with mapped extension    -> [Category]\YYYY\MM\DD\[FileName]
///   3. File with unmapped extension  -> Unmapped\[CapitalizedExt]\YYYY\MM\DD\[FileName]
///   3b. File with no extension       -> Unmapped\No_Extension\YYYY\MM\DD\[FileName]
/// </summary>
public sealed class RoutingEngine
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private readonly ILogger<RoutingEngine> _logger;

    public RoutingEngine(ILogger<RoutingEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Compute the destination path the item *should* end up at, before conflict resolution.
    /// The conflict-resolution layer (FileMover) may append " (1)", " (2)", etc.
    /// </summary>
    public string DetermineDestination(string sourcePath, bool isDirectory, DownganizerConfig config)
    {
        var leaf = Path.GetFileName(sourcePath);
        var date = DateTime.Now;
        var year = date.ToString("yyyy");
        var month = date.ToString("MM");
        var day = date.ToString("dd");

        // ---- Rule 1: Directories are sacred. Move whole, don't peek inside. ----
        if (isDirectory)
        {
            var dest = Path.Combine(config.OutputRoot, "Packages", year, month, day, leaf);
            _logger.LogDebug("Route DIR {Source} -> {Dest}", sourcePath, dest);
            return dest;
        }

        // ---- Rule 3b: No extension at all. ----
        var ext = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
        {
            var dest = Path.Combine(config.OutputRoot, "Unmapped", "No_Extension", year, month, day, leaf);
            _logger.LogDebug("Route NOEXT {Source} -> {Dest}", sourcePath, dest);
            return dest;
        }

        // ---- Rule 2: Look up category for this extension. ----
        // We iterate explicitly so we don't allocate a LINQ pipeline on every file.
        foreach (var kv in config.Categories)
        {
            foreach (var candidate in kv.Value)
            {
                if (string.Equals(candidate.TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase))
                {
                    var dest = Path.Combine(config.OutputRoot, kv.Key, year, month, day, leaf);
                    _logger.LogDebug("Route FILE [{Cat}] {Source} -> {Dest}", kv.Key, sourcePath, dest);
                    return dest;
                }
            }
        }

        // ---- Rule 3: Unknown extension. Dynamically capitalize and route to Unmapped. ----
        var capExt = SanitizeFolderName(Capitalize(ext));
        var unmappedDest = Path.Combine(config.OutputRoot, "Unmapped", capExt, year, month, day, leaf);
        _logger.LogDebug("Route UNMAPPED [{Ext}] {Source} -> {Dest}", capExt, sourcePath, unmappedDest);
        return unmappedDest;
    }

    private static string Capitalize(string ext)
    {
        if (ext.Length == 0) return ext;
        if (ext.Length == 1) return ext.ToUpperInvariant();
        return char.ToUpperInvariant(ext[0]) + ext[1..];
    }

    /// <summary>
    /// Defensive: an extension like "?weird" should never become a folder name on Windows.
    /// Replace any invalid file-name char with underscore.
    /// </summary>
    private static string SanitizeFolderName(string name)
    {
        // Fast path: most extensions are clean.
        if (name.IndexOfAny(InvalidFileNameChars) < 0) return name;

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(InvalidFileNameChars.AsSpan().IndexOf(c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }
}
