using System.Diagnostics;

namespace Downganizer.Models;

/// <summary>
/// One in-flight candidate the QuietPeriodMonitor is watching. Lives only in memory;
/// is removed from the pending dictionary on successful processing or repeated failure.
/// </summary>
[DebuggerDisplay("{Path,nq} (dir={IsDirectory}, lastChange={LastChange})")]
internal sealed class PendingItem
{
    /// <summary>Absolute path to the file or directory at the root of the watched folder.</summary>
    public required string Path { get; init; }

    /// <summary>Whether this item is a directory (treated as an indestructible unit).</summary>
    public required bool IsDirectory { get; init; }

    /// <summary>UTC timestamp the monitor first saw this path.</summary>
    public required DateTime FirstSeen { get; init; }

    /// <summary>UTC timestamp of the most recent observed change. Drives the quiet-period clock.</summary>
    public DateTime LastChange { get; set; }

    /// <summary>Snapshot taken at the most recent rescan; compared on the next rescan.</summary>
    public FileSnapshot LastSnapshot { get; set; }

    /// <summary>Number of times we've tried and failed to process this item. We give up after a threshold.</summary>
    public int FailureCount { get; set; }
}
