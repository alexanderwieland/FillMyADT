using Serilog;
using Serilog.Events;

namespace FillMyADT.Services;

/// <summary>
/// Service for initializing and configuring Serilog logging
/// </summary>
public static class LoggingService
{
    /// <summary>
    /// Initialize Serilog with file and console sinks in AppData
    /// </summary>
    public static void InitializeLogging()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logDirectory = System.IO.Path.Combine(appDataPath, "FillMyADT", "Logs");
        var logFilePath = System.IO.Path.Combine(logDirectory, "log-.txt");

        // Ensure log directory exists
        if (!System.IO.Directory.Exists(logDirectory))
        {
            System.IO.Directory.CreateDirectory(logDirectory);
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "FillMyADT")
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj} {Properties}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("Logging initialized. Log directory: {LogDirectory}", logDirectory);
    }

    /// <summary>
    /// Close and flush the logger
    /// </summary>
    public static void CloseLogging()
    {
        Log.Information("Application shutting down");
        Log.CloseAndFlush();
    }
}
