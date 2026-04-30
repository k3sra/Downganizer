namespace Downganizer.Services;

/// <summary>
/// The actual file/folder mover. Handles:
///   - Creating the destination directory tree on demand.
///   - Conflict resolution: if the destination already exists, append " (1)", " (2)", ...
///     until we find a free name. This guarantees we never overwrite anything.
///   - Cross-volume moves: Directory.Move / File.Move only work within a single volume
///     when the source and destination are on different drives, the OS returns an
///     IOException. We catch that and fall back to copy+delete to preserve semantics.
/// </summary>
public sealed class FileMover
{
    private readonly ILogger<FileMover> _logger;

    public FileMover(ILogger<FileMover> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Move <paramref name="source"/> to <paramref name="destination"/>, resolving
    /// any name conflict by appending " (N)". Returns the actual final path used.
    /// </summary>
    public string MoveWithConflictResolution(string source, string destination, bool isDirectory)
    {
        var destDir = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Invalid destination path: {destination}");

        Directory.CreateDirectory(destDir);

        var finalPath = ResolveConflict(destination, isDirectory);

        if (isDirectory)
        {
            try
            {
                Directory.Move(source, finalPath);
            }
            catch (IOException) when (!IsSameVolume(source, finalPath))
            {
                // Cross-volume: fall back to copy+delete.
                _logger.LogInformation("Cross-volume DIR move detected; copying then deleting: {Source} -> {Dest}",
                    source, finalPath);
                CopyDirectoryThenDelete(source, finalPath);
            }
        }
        else
        {
            try
            {
                File.Move(source, finalPath);
            }
            catch (IOException) when (!IsSameVolume(source, finalPath))
            {
                _logger.LogInformation("Cross-volume FILE move detected; copying then deleting: {Source} -> {Dest}",
                    source, finalPath);
                File.Copy(source, finalPath, overwrite: false);
                File.Delete(source);
            }
        }

        _logger.LogInformation("Moved {Kind}: {Source} -> {Final}",
            isDirectory ? "DIR " : "FILE", source, finalPath);

        return finalPath;
    }

    /// <summary>
    /// If <paramref name="destination"/> already exists, return a new path with
    /// " (1)", " (2)", ... appended until we find one that doesn't collide.
    /// </summary>
    private static string ResolveConflict(string destination, bool isDirectory)
    {
        if (!Exists(destination, isDirectory)) return destination;

        var dir = Path.GetDirectoryName(destination)!;
        string nameStem;
        string ext;
        if (isDirectory)
        {
            nameStem = Path.GetFileName(destination);
            ext = string.Empty;
        }
        else
        {
            nameStem = Path.GetFileNameWithoutExtension(destination);
            ext = Path.GetExtension(destination); // includes the leading "."
        }

        for (int i = 1; i < int.MaxValue; i++)
        {
            var candidate = Path.Combine(dir, $"{nameStem} ({i}){ext}");
            if (!Exists(candidate, isDirectory)) return candidate;
        }

        // Should never happen unless there are 2 billion duplicates - if it did,
        // throwing is the right move so we don't silently overwrite.
        throw new InvalidOperationException(
            $"Could not resolve conflict for {destination} after exhausting integer suffixes.");
    }

    private static bool Exists(string path, bool isDirectory)
        => isDirectory ? Directory.Exists(path) : File.Exists(path);

    private static bool IsSameVolume(string p1, string p2)
    {
        try
        {
            var r1 = Path.GetPathRoot(Path.GetFullPath(p1));
            var r2 = Path.GetPathRoot(Path.GetFullPath(p2));
            return string.Equals(r1, r2, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Cross-volume directory copy + delete fallback. We preserve the directory
    /// tree structure by computing relative paths against the source root.
    /// </summary>
    private static void CopyDirectoryThenDelete(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        // Recreate every subdirectory.
        foreach (var subdir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, subdir);
            Directory.CreateDirectory(Path.Combine(destination, rel));
        }

        // Copy every file. overwrite:false because the destination is a fresh tree.
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            File.Copy(file, Path.Combine(destination, rel), overwrite: false);
        }

        // Only delete after every byte has been written.
        Directory.Delete(source, recursive: true);
    }
}
