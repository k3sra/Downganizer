namespace Downganizer.Configuration;

/// <summary>
/// On-disk locations the service uses. Bound from appsettings.json -> Downganizer:*.
/// Defaults pin everything under C:\Downganizer so the service is self-contained
/// and easy to back up, audit, or wipe.
/// </summary>
public sealed class HostingOptions
{
    /// <summary>Path to the user-facing config.json (watched folder, categories, quiet period).</summary>
    public string ConfigPath { get; set; } = @"C:\Downganizer\config\config.json";

    /// <summary>Directory holding history.json (the JSON state database).</summary>
    public string DataDirectory { get; set; } = @"C:\Downganizer\data";

    /// <summary>Directory holding rolling daily log files.</summary>
    public string LogDirectory { get; set; } = @"C:\Downganizer\logs";

    /// <summary>
    /// Make sure all three directories exist before the rest of the app touches them.
    /// Called once at startup; cheap and idempotent.
    /// </summary>
    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        var configDir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(configDir))
        {
            Directory.CreateDirectory(configDir);
        }
    }
}
