namespace Downganizer.Configuration;

/// <summary>
/// User-facing configuration deserialized from config\config.json.
/// </summary>
public sealed class DownganizerConfig
{
    /// <summary>The single root folder being monitored. Only the top level is watched.</summary>
    public string WatchedFolder { get; set; } = string.Empty;

    /// <summary>
    /// Where organized output goes. Usually equals <see cref="WatchedFolder"/> so categories
    /// live alongside the inbox (e.g. Downloads\Images\2026\04\26\foo.jpg).
    /// </summary>
    public string OutputRoot { get; set; } = string.Empty;

    /// <summary>How long an item must be untouched before we move it. Default 60 seconds.</summary>
    public int QuietPeriodSeconds { get; set; } = 60;

    /// <summary>How often the monitor loop wakes up to check pending items.</summary>
    public int ScanIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Category name -> list of extensions (without the dot, case-insensitive).
    /// Example: "Images" -> ["jpg", "png", "gif"].
    /// </summary>
    public Dictionary<string, List<string>> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
