namespace Downganizer.Models;

/// <summary>
/// Cheap, value-equality snapshot of a file or folder's "state" used by the
/// quiet-period monitor to detect changes between scans.
///
/// For a file:      TotalSize = file size,  MaxMTime = file's mtime,        FileCount = 1.
/// For a directory: TotalSize = sum of all child file sizes (recursively),
///                  MaxMTime = max mtime of any child,
///                  FileCount = total recursive file count.
///
/// We never modify or move based on the recursive scan; it's read-only inspection
/// to know whether the package is still being written.
/// </summary>
internal readonly record struct FileSnapshot(long TotalSize, DateTime MaxMTime, int FileCount)
{
    /// <summary>Sentinel value for "couldn't read this path right now" (locked, vanished, etc.).</summary>
    public static FileSnapshot Unreadable { get; } = new(-1, DateTime.MinValue, -1);

    /// <summary>True if the snapshot was successfully taken.</summary>
    public bool IsValid => TotalSize >= 0;
}
