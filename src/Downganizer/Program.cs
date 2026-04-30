// =============================================================================
// Downganizer entry point.
//
// Wires the .NET generic host to the Windows Service Control Manager,
// loads hosting options, configures three logging sinks (console, EventLog,
// rolling file), registers the Downganizer pipeline as singletons, and runs
// the Worker as a hosted background service.
// =============================================================================

using Downganizer;
using Downganizer.Configuration;
using Downganizer.Logging;
using Downganizer.Services;

var builder = Host.CreateApplicationBuilder(args);

// -----------------------------------------------------------------------------
// 1. Plug into the Windows Service Control Manager.
//    AddWindowsService is a no-op when running interactively (e.g. `dotnet run`)
//    so the same binary works as both a foreground console app for debugging
//    and a real Windows Service in production.
// -----------------------------------------------------------------------------
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Downganizer";
});

// -----------------------------------------------------------------------------
// 2. Bind hosting options (where on disk our state, logs, and config live).
//    These are pulled from appsettings.json -> Downganizer:* but each one has
//    a sensible default pointing at C:\Downganizer\... so the service still
//    starts even if appsettings.json is missing or partially configured.
// -----------------------------------------------------------------------------
var hostingOptions = new HostingOptions();
builder.Configuration.GetSection("Downganizer").Bind(hostingOptions);
hostingOptions.EnsureDirectoriesExist();
builder.Services.AddSingleton(hostingOptions);

// -----------------------------------------------------------------------------
// 3. Logging: three sinks for redundancy.
//    - Console:  visible when running interactively.
//    - EventLog: visible in Event Viewer; this is what the SCM surfaces.
//    - File:     rolling daily file under C:\Downganizer\logs for offline review.
// -----------------------------------------------------------------------------
builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddConsole();
if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog(settings =>
    {
        settings.SourceName = "Downganizer";
        settings.LogName = "Application";
    });
}
builder.Logging.AddProvider(new FileLoggerProvider(hostingOptions.LogDirectory));

// -----------------------------------------------------------------------------
// 4. Downganizer pipeline. Everything is a singleton because we want a single
//    in-memory ConcurrentDictionary of pending items and a single in-memory
//    history database for the lifetime of the process.
// -----------------------------------------------------------------------------
builder.Services.AddSingleton<ConfigLoader>(sp =>
    new ConfigLoader(hostingOptions.ConfigPath, sp.GetRequiredService<ILogger<ConfigLoader>>()));

builder.Services.AddSingleton<HistoryDatabase>(sp =>
    new HistoryDatabase(hostingOptions.DataDirectory, sp.GetRequiredService<ILogger<HistoryDatabase>>()));

builder.Services.AddSingleton<RoutingEngine>();
builder.Services.AddSingleton<FileMover>();
builder.Services.AddSingleton<QuietPeriodMonitor>();
builder.Services.AddHostedService<Worker>();

// -----------------------------------------------------------------------------
// 5. Run. RunAsync blocks until the service is asked to stop by the SCM
//    (or until Ctrl+C in interactive mode).
// -----------------------------------------------------------------------------
var host = builder.Build();
await host.RunAsync();
