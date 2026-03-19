using System.Text.Json;
using System.Text.Json.Serialization;
using FillMyADT.Models.Configuration;
using Serilog;

namespace FillMyADT.Services;

/// <summary>
/// Service for managing application configuration stored in AppData
/// </summary>
public class ConfigurationService
{
    private readonly string _configDirectory;
    private readonly string _configFilePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public ConfigurationService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _configDirectory = System.IO.Path.Combine(appDataPath, "FillMyADT");
        _configFilePath = System.IO.Path.Combine(_configDirectory, "config.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        EnsureConfigDirectoryExists();
    }

    /// <summary>
    /// Get the configuration directory path
    /// </summary>
    public string ConfigDirectory => _configDirectory;

    /// <summary>
    /// Load configuration for a specific event source type
    /// </summary>
    public async Task<T?> LoadConfigAsync<T>(CancellationToken cancellationToken = default) where T : EventSourceConfig
    {
        try
        {
            if (!System.IO.File.Exists(_configFilePath))
            {
                Log.Information("Configuration file not found, using defaults");
                return null;
            }

            var json = await System.IO.File.ReadAllTextAsync(_configFilePath, cancellationToken).ConfigureAwait(false);
            var config = JsonSerializer.Deserialize<AppConfiguration>(json, _jsonOptions);

            if (config?.EventSources == null)
                return null;

            var sourceType = typeof(T).Name;
            var sourceConfig = config.EventSources.FirstOrDefault(c => c.GetType().Name == sourceType);

            return sourceConfig as T;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading configuration for {SourceType}", typeof(T).Name);
            return null;
        }
    }

    /// <summary>
    /// Save configuration for a specific event source
    /// </summary>
    public async Task SaveConfigAsync<T>(T config, CancellationToken cancellationToken = default) where T : EventSourceConfig
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            var appConfig = await LoadAllConfigsAsync(cancellationToken).ConfigureAwait(false) ?? new AppConfiguration();

            var existingIndex = appConfig.EventSources.FindIndex(c => c.SourceType == config.SourceType);
            if (existingIndex >= 0)
            {
                appConfig.EventSources[existingIndex] = config;
            }
            else
            {
                appConfig.EventSources.Add(config);
            }

            var json = JsonSerializer.Serialize(appConfig, _jsonOptions);
            await System.IO.File.WriteAllTextAsync(_configFilePath, json, cancellationToken).ConfigureAwait(false);

            Log.Information("Configuration saved for {SourceType}", config.SourceType);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving configuration for {SourceType}", config.SourceType);
            throw;
        }
    }

    /// <summary>
    /// Load all configurations
    /// </summary>
    public async Task<AppConfiguration?> LoadAllConfigsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!System.IO.File.Exists(_configFilePath))
            {
                return CreateDefaultConfiguration();
            }

            var json = await System.IO.File.ReadAllTextAsync(_configFilePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AppConfiguration>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading all configurations");
            return CreateDefaultConfiguration();
        }
    }

    /// <summary>
    /// Initialize configuration file with defaults if it doesn't exist
    /// </summary>
    public async Task InitializeDefaultConfigAsync(CancellationToken cancellationToken = default)
    {
        if (System.IO.File.Exists(_configFilePath))
        {
            Log.Information("Configuration file already exists at {Path}", _configFilePath);
            return;
        }

        var defaultConfig = CreateDefaultConfiguration();
        var json = JsonSerializer.Serialize(defaultConfig, _jsonOptions);
        await System.IO.File.WriteAllTextAsync(_configFilePath, json, cancellationToken).ConfigureAwait(false);

        Log.Information("Default configuration created at {Path}", _configFilePath);
    }

    private AppConfiguration CreateDefaultConfiguration()
    {
        return new AppConfiguration
        {
            EventSources =
            [
                new WindowsEventSourceConfig
                {
                    IsEnabled = true
                },
                new GitEventSourceConfig
                {
                    IsEnabled = true,
                    AutoDiscoverRepositories = true,
                    ScanDirectory = @"C:\Work\Sync\Git\",
                    FilterByRecentActivity = true,
                    IncludeCommits = true,
                    IncludeBranchSwitches = true
                },
                new OutlookEventSourceConfig
                {
                    IsEnabled = true,
                    RequireRunningInstance = false,
                    IncludeTeamsMeetings = true,
                    IncludeAllDayEvents = false
                }
            ]
        };
    }

    private void EnsureConfigDirectoryExists()
    {
        if (!System.IO.Directory.Exists(_configDirectory))
        {
            System.IO.Directory.CreateDirectory(_configDirectory);
            Log.Information("Configuration directory created at {Path}", _configDirectory);
        }
    }
}

/// <summary>
/// Root application configuration
/// </summary>
public class AppConfiguration
{
    public List<EventSourceConfig> EventSources { get; set; } = [];
}
