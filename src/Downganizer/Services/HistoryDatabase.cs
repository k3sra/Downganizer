using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Downganizer.Models;

namespace Downganizer.Services;

/// <summary>
/// Thread-safe, JSON-only "manual override" memory.
///
/// Strategy:
///   - On startup, slurp history.json into a <see cref="ConcurrentDictionary{TKey, TValue}"/>.
///   - On every successful organize, append/update the in-memory dictionary and atomically
///     flush the whole thing back to disk (write to .tmp, then File.Move with overwrite).
///   - File.Move on Windows uses MoveFileEx with MOVEFILE_REPLACE_EXISTING, which is an
///     atomic rename within a volume. The main file is never half-written: either the old
///     version or the new version exists, never something in between.
///   - On startup, if we find history.json missing but history.json.tmp present
///     (rare crash window), we recover from the tmp file.
///   - If history.json is corrupt (e.g. user edit gone wrong), we back it up rather
///     than crash the service, so a single bad write can never permanently disable us.
///
/// Hashing: SHA-256 of "{kind}|{name}|{size}". This is the "manual override" identity:
/// if a file with the same name/size and same kind shows up again later, we recognize it
/// and skip it - this is the contract that lets the user manually move things back into
/// Downloads without us re-organizing them in a loop.
/// </summary>
public sealed class HistoryDatabase
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly string _tempPath;
    private readonly ILogger<HistoryDatabase> _logger;

    // ConcurrentDictionary - all reads from any thread are lock-free; writes serialize internally.
    private readonly ConcurrentDictionary<string, HistoryEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    // SemaphoreSlim guards the disk flush so two concurrent RecordAsync calls don't
    // race on the temp file.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public HistoryDatabase(string dataDir, ILogger<HistoryDatabase> logger)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "history.json");
        _tempPath = _path + ".tmp";
        _logger = logger;
        Load();
    }

    /// <summary>Number of remembered entries. Useful for diagnostics/health checks.</summary>
    public int Count => _entries.Count;

    /// <summary>True if we've seen this hash before.</summary>
    public bool HasProcessed(string hash) => _entries.ContainsKey(hash);

    /// <summary>Try to look up an entry by hash, for richer log messages on skip.</summary>
    public bool TryGetEntry(string hash, out HistoryEntry? entry)
    {
        var found = _entries.TryGetValue(hash, out var e);
        entry = e;
        return found;
    }

    /// <summary>Add (or replace) an entry and flush atomically to disk.</summary>
    public async Task RecordAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        _entries[entry.Hash] = entry;
        await FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Compute the canonical hash. The "{kind}|" prefix means a file and a folder
    /// with the same name will hash to different values - they're different identities.
    /// </summary>
    public static string ComputeHash(string name, long size, bool isDirectory)
    {
        var input = $"{(isDirectory ? "D" : "F")}|{name}|{size}";
        Span<byte> hash = stackalloc byte[32];
        var bytes = Encoding.UTF8.GetBytes(input);
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash);
    }

    // ------------------------------------------------------------------------
    // Disk I/O
    // ------------------------------------------------------------------------

    private void Load()
    {
        // Crash recovery: if main is missing but tmp is present, the prior process died
        // mid-flush. The tmp file is the freshest committed snapshot we have.
        if (!File.Exists(_path) && File.Exists(_tempPath))
        {
            try
            {
                File.Move(_tempPath, _path);
                _logger.LogWarning("Recovered history.json from leftover .tmp file (prior crash mid-flush)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover history from .tmp file");
            }
        }

        if (!File.Exists(_path))
        {
            _logger.LogInformation("No history file at {Path}; starting with an empty database", _path);
            return;
        }

        try
        {
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogInformation("History file at {Path} is empty; starting fresh", _path);
                return;
            }

            var entries = JsonSerializer.Deserialize<Dictionary<string, HistoryEntry>>(json, JsonOpts);
            if (entries != null)
            {
                foreach (var kv in entries)
                {
                    _entries[kv.Key] = kv.Value;
                }
            }
            _logger.LogInformation("Loaded {Count} history entries from {Path}", _entries.Count, _path);
        }
        catch (Exception ex)
        {
            // Corrupted history must never take the service down. Quarantine and continue.
            var backup = _path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            try { File.Move(_path, backup); } catch { /* best effort */ }
            _logger.LogError(ex,
                "history.json was corrupt or unreadable; quarantined as {Backup}. Starting fresh.",
                backup);
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Serialize once, write to temp, then atomically replace.
            var json = JsonSerializer.Serialize(_entries, JsonOpts);
            await File.WriteAllTextAsync(_tempPath, json, ct).ConfigureAwait(false);

            // File.Move with overwrite on Windows = MoveFileEx + MOVEFILE_REPLACE_EXISTING.
            // This is the atomic-rename primitive: either the old file is in place or the
            // new file is in place. Power loss mid-call cannot leave a partial file.
            File.Move(_tempPath, _path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
