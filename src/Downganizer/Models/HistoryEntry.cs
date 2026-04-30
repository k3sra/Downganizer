namespace Downganizer.Models;

/// <summary>
/// One entry in history.json. Keyed by <see cref="Hash"/> in the database dictionary.
/// Public so System.Text.Json can serialize it without reflection workarounds.
/// </summary>
public sealed class HistoryEntry
{
    /// <summary>SHA-256 of "{kind}|{name}|{size}". The dictionary key.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>The original file or folder name (just the leaf, no path).</summary>
    public string OriginalName { get; set; } = string.Empty;

    /// <summary>For files: file size. For directories: total recursive size of all child files.</summary>
    public long OriginalSize { get; set; }

    /// <summary>UTC time we processed it (so the user can audit history).</summary>
    public DateTime ProcessedAtUtc { get; set; }

    /// <summary>Where we ultimately moved it.</summary>
    public string FinalPath { get; set; } = string.Empty;

    /// <summary>True if the original was a directory (package).</summary>
    public bool IsDirectory { get; set; }
}
