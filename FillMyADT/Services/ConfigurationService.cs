using FillMyADT.Models;
using FillMyADT.Models.Configuration;
using Serilog;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FillMyADT.Services;

/// <summary>
/// Loads and saves the application configuration stored in LocalAppData and notifies listeners on changes.
/// </summary>
public class ConfigurationService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ConfigurationService>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _configFilePath;
    private readonly bool _isPersistent = true;

    public ConfigurationService()
    {
        var configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FillMyADT");
        Directory.CreateDirectory(configDirectory);
        _configFilePath = Path.Combine(configDirectory, "config.json");
        Current = Load();
    }

    /// <summary>
    /// Create an in-memory configuration service that never reads or writes the config file
    /// </summary>
    public ConfigurationService(AppConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configFilePath = string.Empty;
        _isPersistent = false;
        Current = Clone(configuration);
    }

    /// <summary>
    /// The active configuration. Treat as read-only; use <see cref="CreateEditableCopy"/> and <see cref="Save"/> to change it.
    /// </summary>
    public AppConfiguration Current { get; private set; }

    /// <summary>
    /// Full path of the configuration file
    /// </summary>
    public string ConfigFilePath => _configFilePath;

    /// <summary>
    /// Raised after a new configuration has been saved and activated
    /// </summary>
    public event Action<AppConfiguration>? Changed;

    /// <summary>
    /// Create a deep copy of the current configuration for editing
    /// </summary>
    public AppConfiguration CreateEditableCopy() => Clone(Current);

    /// <summary>
    /// Create a configuration with default values
    /// </summary>
    public static AppConfiguration CreateDefaultConfiguration() => new();

    /// <summary>
    /// Persist the configuration, activate it and notify listeners
    /// </summary>
    public void Save(AppConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var snapshot = Clone(configuration);
        if (_isPersistent)
        {
            File.WriteAllText(_configFilePath, JsonSerializer.Serialize(snapshot, JsonOptions));
            Log.Information("Configuration saved to {Path}", _configFilePath);
        }

        Current = snapshot;
        Changed?.Invoke(snapshot);
    }

    private AppConfiguration Load()
    {
        try
        {
            if (!File.Exists(_configFilePath))
            {
                File.WriteAllText(_configFilePath, JsonSerializer.Serialize(CreateDefaultConfiguration(), JsonOptions));
                Log.Information("Default configuration created at {Path}", _configFilePath);
            }

            var config = JsonSerializer.Deserialize<AppConfiguration>(File.ReadAllText(_configFilePath), JsonOptions);
            Log.Information("Configuration loaded from {Path}", _configFilePath);
            return config ?? CreateDefaultConfiguration();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Error loading configuration, using defaults");
            return CreateDefaultConfiguration();
        }
    }

    private static AppConfiguration Clone(AppConfiguration configuration) =>
        JsonSerializer.Deserialize<AppConfiguration>(JsonSerializer.Serialize(configuration, JsonOptions), JsonOptions)!;
}

/// <summary>
/// Root application configuration
/// </summary>
public class AppConfiguration
{
    /// <summary>
    /// User initials for event names and the ADT export (e.g., "apw")
    /// </summary>
    public string Initials { get; set; } = "apw";

    /// <summary>
    /// Default work hours for holiday/vacation days (e.g., 6.4 for 6 hours 24 minutes)
    /// </summary>
    public double WorkHours { get; set; } = 6.4;

    /// <summary>
    /// Default location for time slots when not in home office
    /// </summary>
    public Location DefaultLocation { get; set; } = Location.WV;

    public WindowsEventSourceConfig Windows { get; set; } = new();

    public GitEventSourceConfig Git { get; set; } = new() { ScanDirectory = @"C:\Work\Sync\Git\" };

    public OutlookEventSourceConfig Outlook { get; set; } = new();

    public EdgeEventSourceConfig Edge { get; set; } = new();
}
