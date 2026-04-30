using System.Collections.Concurrent;
using Downganizer.Configuration;
using Downganizer.Models;

namespace Downganizer.Services;

/// <summary>
/// The brain of the service. Owns the in-memory dictionary of pending items,
/// runs the periodic rescan loop, enforces the 60-second deep quiet period,
/// and dispatches to the routing/move/history pipeline once an item is "settled".
///
/// Why poll on top of FileSystemWatcher events?
///   - FSW with IncludeSubdirectories=false (which we MUST use) cannot tell us when
///     files inside a top-level subfolder change. A torrent client writing to
///     Downloads\my-game\disc1.bin produces zero root-level events.
///   - To detect "package is still being written", we need to actively snapshot
///     (size, mtime, file count) of the top-level item every few seconds and only
///     declare it quiet when the snapshot has been stable for QuietPeriodSeconds.
///   - FSW also occasionally misses events under load. The poll loop is the safety net.
///
/// What "deep quiet" means:
///   - For a file: file size + mtime unchanged for N seconds AND not currently locked.
///   - For a directory: snapshot (total recursive size + max mtime + file count)
///     unchanged for N seconds AND no file inside is locked.
/// </summary>
public sealed class QuietPeriodMonitor
{
    private readonly ConcurrentDictionary<string, PendingItem> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly RoutingEngine _routing;
    private readonly FileMover _mover;
    private readonly HistoryDatabase _history;
    private readonly ILogger<QuietPeriodMonitor> _logger;

    private DownganizerConfig _config = null!; // set via Configure() before RunAsync()

    /// <summary>Maximum failed processing attempts before we drop an item from the queue.</summary>
    private const int MaxFailures = 5;

    public QuietPeriodMonitor(
        RoutingEngine routing,
        FileMover mover,
        HistoryDatabase history,
        ILogger<QuietPeriodMonitor> logger)
    {
        _routing = routing;
        _mover = mover;
        _history = history;
        _logger = logger;
    }

    /// <summary>Bind the loaded config. Called once by the Worker before <see cref="RunAsync"/>.</summary>
    public void Configure(DownganizerConfig config) => _config = config;

    /// <summary>How many items are currently being watched. Useful for diagnostics.</summary>
    public int PendingCount => _pending.Count;

    // ------------------------------------------------------------------------
    // Public API used by the Worker / FileSystemWatcher callbacks
    // ------------------------------------------------------------------------

    /// <summary>
    /// Add or refresh a candidate. Idempotent: calling it many times with the same path
    /// just resets the quiet clock if the snapshot has changed.
    /// </summary>
    public void Track(string path)
    {
        var isDir = Directory.Exists(path);
        var isFile = File.Exists(path);
        if (!isDir && !isFile)
        {
            // Transient: created+deleted between the FSW event and our reaction. Ignore.
            return;
        }

        // Skip our own state files in case anyone drops them at root level
        // (e.g. shouldn't happen, but defense in depth).
        var name = Path.GetFileName(path);
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var snap = TakeSnapshot(path, isDir);
        var now = DateTime.UtcNow;

        _pending.AddOrUpdate(
            path,
            // First time we see this path:
            _ =>
            {
                _logger.LogDebug("Tracking new {Kind}: {Path}", isDir ? "DIR" : "FILE", path);
                return new PendingItem
                {
                    Path = path,
                    IsDirectory = isDir,
                    FirstSeen = now,
                    LastChange = now,
                    LastSnapshot = snap,
                };
            },
            // Update existing tracking entry:
            (_, existing) =>
            {
                if (!snap.Equals(existing.LastSnapshot))
                {
                    existing.LastSnapshot = snap;
                    existing.LastChange = now;
                }
                return existing;
            });
    }

    /// <summary>Remove a candidate (e.g. it was deleted before we could process it).</summary>
    public void Untrack(string path)
    {
        if (_pending.TryRemove(path, out _))
        {
            _logger.LogDebug("Untracked: {Path}", path);
        }
    }

    // ------------------------------------------------------------------------
    // Background loop
    // ------------------------------------------------------------------------

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (_config is null)
            throw new InvalidOperationException("Configure() must be called before RunAsync().");

        _logger.LogInformation(
            "QuietPeriodMonitor started. quiet={Quiet}s, scan-interval={Scan}s",
            _config.QuietPeriodSeconds, _config.ScanIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.ScanIntervalSeconds), stoppingToken)
                    .ConfigureAwait(false);

                RescanPending();
                await ProcessReadyAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the loop die. A single iteration's failure must not
                // disable the service.
                _logger.LogError(ex, "Monitor loop iteration failed; continuing");
            }
        }

        _logger.LogInformation("QuietPeriodMonitor stopped");
    }

    // ------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------

    /// <summary>
    /// For every pending item: take a fresh snapshot, compare to the stored one,
    /// and if anything changed, reset the quiet clock. This is what catches the
    /// case where a torrent is still writing into a subfolder we cannot watch directly.
    /// </summary>
    private void RescanPending()
    {
        foreach (var kv in _pending.ToArray())
        {
            var path = kv.Key;
            var item = kv.Value;

            // The item might have been deleted (user dragged it away, etc.) - drop it.
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                _pending.TryRemove(path, out _);
                continue;
            }

            var snap = TakeSnapshot(path, item.IsDirectory);
            if (!snap.Equals(item.LastSnapshot))
            {
                item.LastSnapshot = snap;
                item.LastChange = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// For every pending item that has been quiet long enough and isn't locked,
    /// try to process it.
    /// </summary>
    private async Task ProcessReadyAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var quietThreshold = TimeSpan.FromSeconds(_config.QuietPeriodSeconds);

        // Snapshot the candidate list so we can mutate the dictionary while iterating.
        var ready = _pending.Values
            .Where(p => p.LastSnapshot.IsValid)
            .Where(p => (now - p.LastChange) >= quietThreshold)
            .ToList();

        foreach (var item in ready)
        {
            if (ct.IsCancellationRequested) break;

            // Final lock check - even if the snapshot is stable, the file might have
            // been opened with an exclusive lock by something (antivirus, indexer, etc.).
            // A quick try-open with FileShare.None tells us cheaply.
            if (IsAnyFileLocked(item.Path, item.IsDirectory))
            {
                _logger.LogDebug("Lock detected on {Path}; deferring", item.Path);
                item.LastChange = DateTime.UtcNow;
                continue;
            }

            try
            {
                await ProcessOneAsync(item, ct).ConfigureAwait(false);
                _pending.TryRemove(item.Path, out _);
            }
            catch (Exception ex)
            {
                item.FailureCount++;
                _logger.LogError(ex, "Failed to process {Path} (attempt {N})", item.Path, item.FailureCount);

                if (item.FailureCount >= MaxFailures)
                {
                    _logger.LogWarning(
                        "Giving up on {Path} after {N} failures; will retry only if a new event arrives",
                        item.Path, item.FailureCount);
                    _pending.TryRemove(item.Path, out _);
                }
                else
                {
                    // Restart the quiet clock so we wait a fresh QuietPeriodSeconds before retrying.
                    item.LastChange = DateTime.UtcNow;
                }
            }
        }
    }

    private async Task ProcessOneAsync(PendingItem item, CancellationToken ct)
    {
        var name = Path.GetFileName(item.Path);
        var hash = HistoryDatabase.ComputeHash(name, item.LastSnapshot.TotalSize, item.IsDirectory);

        // ---- Manual override check ----
        // If the user moved an organized item back to Downloads (or a duplicate-by-identity
        // shows up), skip it. Don't loop the user.
        if (_history.TryGetEntry(hash, out var prior) && prior != null)
        {
            _logger.LogInformation(
                "Skipping {Path} - matches prior history (manual override). Previous final path was {Final}",
                item.Path, prior.FinalPath);
            // Untrack so we don't re-evaluate it every scan cycle.
            return;
        }

        // ---- Route + move ----
        var dest = _routing.DetermineDestination(item.Path, item.IsDirectory, _config);
        var finalPath = _mover.MoveWithConflictResolution(item.Path, dest, item.IsDirectory);

        // ---- Persist to history (atomic JSON write inside) ----
        await _history.RecordAsync(new HistoryEntry
        {
            Hash = hash,
            OriginalName = name,
            OriginalSize = item.LastSnapshot.TotalSize,
            ProcessedAtUtc = DateTime.UtcNow,
            FinalPath = finalPath,
            IsDirectory = item.IsDirectory,
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------
    // Snapshot + lock probing
    // ------------------------------------------------------------------------

    /// <summary>
    /// Take a value-equality snapshot of the path. For a directory, this enumerates
    /// every file recursively to compute (total size, max mtime, file count). We never
    /// modify or move based on this enumeration - it's read-only inspection only,
    /// and the directory itself is moved as one indestructible unit.
    /// </summary>
    private static FileSnapshot TakeSnapshot(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                long total = 0;
                DateTime maxMtime = DateTime.MinValue;
                int count = 0;

                var di = new DirectoryInfo(path);
                foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    total += f.Length;
                    if (f.LastWriteTimeUtc > maxMtime) maxMtime = f.LastWriteTimeUtc;
                    count++;
                }

                // Empty directory: use the directory's own mtime so we still detect
                // changes (e.g. the first file being created inside).
                if (count == 0)
                {
                    maxMtime = di.LastWriteTimeUtc;
                }

                return new FileSnapshot(total, maxMtime, count);
            }
            else
            {
                var fi = new FileInfo(path);
                return new FileSnapshot(fi.Length, fi.LastWriteTimeUtc, 1);
            }
        }
        catch
        {
            // The path was locked, vanished, or perms changed mid-scan. Mark unreadable
            // so the monitor knows not to act on this snapshot.
            return FileSnapshot.Unreadable;
        }
    }

    private static bool IsAnyFileLocked(string path, bool isDirectory)
    {
        if (!isDirectory) return IsFileLocked(path);

        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (IsFileLocked(f)) return true;
            }
            return false;
        }
        catch
        {
            // If we can't even enumerate, something is going on - assume locked.
            return true;
        }
    }

    private static bool IsFileLocked(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
