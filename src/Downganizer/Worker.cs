using Downganizer.Configuration;
using Downganizer.Services;

namespace Downganizer;

/// <summary>
/// The hosted background service. Owns the FileSystemWatcher, runs the initial
/// scan to pick up anything that was already in the watched folder when the service
/// started, and keeps the QuietPeriodMonitor loop alive until the SCM asks us to stop.
///
/// Critical contract: the FileSystemWatcher MUST be set up with IncludeSubdirectories=false.
/// We never recurse to pull files out of folders. A folder dropped into Downloads is moved
/// as a single atomic unit. The QuietPeriodMonitor does read-only recursive snapshots
/// purely to detect "is this folder still being written into?" - never to act on the contents.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ConfigLoader _configLoader;
    private readonly QuietPeriodMonitor _monitor;

    private DownganizerConfig _config = null!;
    private FileSystemWatcher? _watcher;

    public Worker(
        ILogger<Worker> logger,
        ConfigLoader configLoader,
        QuietPeriodMonitor monitor)
    {
        _logger = logger;
        _configLoader = configLoader;
        _monitor = monitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Downganizer worker starting on {Host}", Environment.MachineName);

        try
        {
            _config = _configLoader.Load();
            _monitor.Configure(_config);

            // The watched folder must exist. If it doesn't, create it - lets the
            // service install cleanly even when there's no Downloads folder yet.
            Directory.CreateDirectory(_config.WatchedFolder);
            Directory.CreateDirectory(_config.OutputRoot);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Configuration load failed - service cannot start");
            throw;
        }

        // Spin up the monitor loop in the background. It will block on Task.Delay until
        // the cancellation token is signalled by the SCM.
        var monitorTask = _monitor.RunAsync(stoppingToken);

        // ---- Order matters ----
        //   1. Arm the watcher first so any new event between now and the initial scan
        //      is captured.
        //   2. Then do the initial scan to pick up items already on disk. Track() is
        //      idempotent, so any double-counting is harmless.
        SetupWatcher();
        InitialScan();

        try
        {
            await monitorTask.ConfigureAwait(false);
        }
        finally
        {
            _watcher?.Dispose();
            _watcher = null;
            _logger.LogInformation("Downganizer worker stopped (pending={Pending})", _monitor.PendingCount);
        }
    }

    // ------------------------------------------------------------------------
    // Initial scan
    // ------------------------------------------------------------------------

    private void InitialScan()
    {
        try
        {
            int count = 0;

            // Top-level files only.
            foreach (var f in Directory.EnumerateFiles(_config.WatchedFolder))
            {
                _monitor.Track(f);
                count++;
            }

            // Top-level directories only - never recurse into them. The directory itself
            // is the unit of organization.
            foreach (var d in Directory.EnumerateDirectories(_config.WatchedFolder))
            {
                if (IsOurOutputFolder(d)) continue; // skip our own category folders
                _monitor.Track(d);
                count++;
            }

            _logger.LogInformation("Initial scan found {Count} candidates in {Path}",
                count, _config.WatchedFolder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial scan failed; service will continue with watcher-only events");
        }
    }

    /// <summary>
    /// When OutputRoot == WatchedFolder (the default - both are Downloads), our own
    /// category folders (Images, Videos, Packages, Unmapped, ...) live at the root of
    /// the watched folder. The watcher will see them and we must skip them - otherwise
    /// we'd try to "organize" our own output.
    /// </summary>
    private bool IsOurOutputFolder(string path)
    {
        if (!string.Equals(
                Path.GetFullPath(_config.OutputRoot).TrimEnd('\\'),
                Path.GetFullPath(_config.WatchedFolder).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        if (string.Equals(name, "Packages", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "Unmapped", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var category in _config.Categories.Keys)
        {
            if (string.Equals(category, name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    // ------------------------------------------------------------------------
    // FileSystemWatcher
    // ------------------------------------------------------------------------

    private void SetupWatcher()
    {
        _watcher = new FileSystemWatcher(_config.WatchedFolder)
        {
            // CRITICAL: only watch the root level. Never look inside subfolders.
            // This is the contract that lets a torrent client write into a subfolder
            // for hours without us ever getting an event from inside it.
            IncludeSubdirectories = false,

            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size,

            // Bigger buffer = fewer dropped events under heavy load. 64 KiB is the
            // recommended ceiling for non-network paths.
            InternalBufferSize = 64 * 1024,
        };

        _watcher.Created += OnCreated;
        _watcher.Renamed += OnRenamed;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Error   += OnError;

        _watcher.EnableRaisingEvents = true;

        _logger.LogInformation("FileSystemWatcher armed on {Path} (root level only, no recursion)",
            _config.WatchedFolder);
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (IsOurOutputFolder(e.FullPath)) return;
        _monitor.Track(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Common pattern: download.crdownload -> download.zip, or torrent.part -> torrent.mkv.
        // The old name (under tracking) goes away; the new name starts a fresh quiet clock.
        _monitor.Untrack(e.OldFullPath);
        if (!IsOurOutputFolder(e.FullPath))
        {
            _monitor.Track(e.FullPath);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // For root-level files this fires on every write while the file is growing.
        // Each one resets the quiet clock via Track()'s snapshot comparison.
        if (IsOurOutputFolder(e.FullPath)) return;
        _monitor.Track(e.FullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        _monitor.Untrack(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // FileSystemWatcher can drop its internal buffer under sustained heavy load.
        // Re-arm it and re-do the initial scan so we don't miss anything that arrived
        // during the gap.
        _logger.LogError(e.GetException(), "FileSystemWatcher error - rearming");
        try
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            SetupWatcher();
            InitialScan();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to rearm watcher; service is in a degraded state");
        }
    }
}
