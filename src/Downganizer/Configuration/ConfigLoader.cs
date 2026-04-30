using System.Text.Json;

namespace Downganizer.Configuration;

/// <summary>
/// Reads, validates, and (if missing) seeds the user-facing config.json.
/// Designed to never throw a hard error on first run: if the config doesn't
/// exist yet, we write a sensible default and read it back.
/// </summary>
public sealed class ConfigLoader
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly ILogger<ConfigLoader> _logger;

    public ConfigLoader(string path, ILogger<ConfigLoader> logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>
    /// Load (or seed) the configuration. Throws only on truly invalid config
    /// (e.g. missing required fields after seeding succeeds).
    /// </summary>
    public DownganizerConfig Load()
    {
        if (!File.Exists(_path))
        {
            _logger.LogWarning("Config not found at {Path}; writing default", _path);
            WriteDefault();
        }

        string json = File.ReadAllText(_path);
        var config = JsonSerializer.Deserialize<DownganizerConfig>(json, ReadOpts)
            ?? throw new InvalidOperationException($"Config at {_path} deserialized to null");

        Validate(config);

        _logger.LogInformation(
            "Config loaded: Watched={Watched}, OutputRoot={Output}, Quiet={Quiet}s, Scan={Scan}s, Categories={CatCount}",
            config.WatchedFolder, config.OutputRoot, config.QuietPeriodSeconds,
            config.ScanIntervalSeconds, config.Categories.Count);

        return config;
    }

    private static void Validate(DownganizerConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.WatchedFolder))
            throw new InvalidOperationException("Config: 'WatchedFolder' is required.");
        if (string.IsNullOrWhiteSpace(c.OutputRoot))
            throw new InvalidOperationException("Config: 'OutputRoot' is required.");
        if (c.QuietPeriodSeconds < 1)
            throw new InvalidOperationException("Config: 'QuietPeriodSeconds' must be >= 1.");
        if (c.ScanIntervalSeconds < 1)
            throw new InvalidOperationException("Config: 'ScanIntervalSeconds' must be >= 1.");

        // Make sure both directories exist; create if missing rather than failing,
        // because this lets the service self-heal on first install.
        Directory.CreateDirectory(c.WatchedFolder);
        Directory.CreateDirectory(c.OutputRoot);
    }

    private void WriteDefault()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        var defaultConfig = new DownganizerConfig
        {
            WatchedFolder = downloads,
            OutputRoot = downloads,
            QuietPeriodSeconds = 60,
            ScanIntervalSeconds = 5,
            Categories = DefaultCategories(),
        };

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(defaultConfig, WriteOpts));
        _logger.LogInformation("Wrote default config to {Path}", _path);
    }

    /// <summary>
    /// Default extension map. Same set the bundled config\config.json ships with;
    /// duplicated here so a fresh install with no config file still works.
    /// </summary>
    private static Dictionary<string, List<string>> DefaultCategories() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Images"]        = new() { "jpg", "jpeg", "png", "gif", "bmp", "webp", "svg", "ico", "tiff", "tif", "heic", "heif", "raw", "cr2", "nef", "arw" },
        ["Videos"]        = new() { "mp4", "mkv", "avi", "mov", "webm", "wmv", "flv", "m4v", "mpg", "mpeg", "3gp", "ts" },
        ["Audio"]         = new() { "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus", "aiff", "alac" },
        ["Documents"]     = new() { "pdf", "doc", "docx", "txt", "rtf", "odt", "md", "epub", "mobi", "azw3", "djvu" },
        ["Spreadsheets"]  = new() { "xls", "xlsx", "xlsm", "csv", "ods", "tsv" },
        ["Presentations"] = new() { "ppt", "pptx", "odp", "key" },
        ["Archives"]      = new() { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "tgz", "tbz2", "lz4", "zst" },
        ["DiskImages"]    = new() { "iso", "img", "vhd", "vhdx", "dmg", "cue", "bin", "nrg" },
        ["Installers"]    = new() { "exe", "msi", "msix", "appx", "appinstaller" },
        ["Scripts"]       = new() { "bat", "cmd", "ps1", "sh", "vbs", "py", "rb", "pl" },
        ["Code"]          = new() { "cs", "js", "ts", "jsx", "tsx", "java", "cpp", "c", "h", "hpp", "html", "css", "scss", "json", "xml", "yaml", "yml", "go", "rs", "kt", "swift", "php" },
        ["Fonts"]         = new() { "ttf", "otf", "woff", "woff2", "eot" },
        ["Torrents"]      = new() { "torrent" },
        ["Models3D"]      = new() { "obj", "fbx", "blend", "stl", "3ds", "glb", "gltf", "dae" },
        ["Design"]        = new() { "psd", "ai", "sketch", "fig", "xd", "indd" },
    };
}
