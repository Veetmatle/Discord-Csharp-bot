using Serilog;
using Serilog.Events;

namespace Discord_Bot_AI.Services;

/// <summary>
/// Provides centralized logging configuration using Serilog for structured logging.
/// </summary>
public static class LoggingService
{
    private static bool _initialized;

    /// <summary>
    /// Initializes the global Serilog logger with console and file sinks.
    /// </summary>
    /// <param name="logPath">The directory path for log files.</param>
    public static void Initialize(string logPath = "logs")
    {
        if (_initialized) return;
        
        Directory.CreateDirectory(logPath);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(logPath, "bot-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _initialized = true;
        Log.Information("Logging initialized with log path: {LogPath}", logPath);
    }

    public static void Shutdown()
    {
        Log.Information("Shutting down logging...");
        Log.CloseAndFlush();
    }
}
