using FillMyADT.Models.Configuration;
using Serilog;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FillMyADT.Services;

/// <summary>
/// Loads the application configuration stored in LocalAppData, creating a default file on first run.
/// </summary>
public class ConfigurationService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ConfigurationService>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new EventSourceConfigConverter() }
    };

    private readonly string _configFilePath;

    public ConfigurationService()
    {
        var configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FillMyADT");
        Directory.CreateDirectory(configDirectory);
        _configFilePath = Path.Combine(configDirectory, "config.json");
    }

    /// <summary>
    /// Load the configuration, writing defaults first if no file exists. Falls back to defaults on errors.
    /// </summary>
    public AppConfiguration Load()
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

    private static AppConfiguration CreateDefaultConfiguration() => new()
    {
        EventSources =
        [
            new WindowsEventSourceConfig(),
            new GitEventSourceConfig { ScanDirectory = @"C:\Work\Sync\Git\" },
            new OutlookEventSourceConfig()
        ]
    };
}

/// <summary>
/// Root application configuration
/// </summary>
public class AppConfiguration
{
    /// <summary>
    /// User initials for event names (e.g., "apw")
    /// </summary>
    public string Initials { get; set; } = "apw";

    /// <summary>
    /// Default work hours for holiday/vacation days (e.g., 6.4 for 6 hours 24 minutes)
    /// </summary>
    public double WorkHours { get; set; } = 6.4;

    /// <summary>
    /// Default location for time slots when not in home office
    /// </summary>
    public string DefaultLocation { get; set; } = "WV";

    /// <summary>
    /// Event source configurations
    /// </summary>
    public List<EventSourceConfig> EventSources { get; set; } = [];

    /// <summary>
    /// Get the configuration for an event source type, or its defaults if not configured
    /// </summary>
    public T GetSourceConfig<T>() where T : EventSourceConfig, new() =>
        EventSources.OfType<T>().FirstOrDefault() ?? new T();
}

/// <summary>
/// Custom JSON converter for EventSourceConfig to handle polymorphism
/// </summary>
public class EventSourceConfigConverter : JsonConverter<EventSourceConfig>
{
    public override EventSourceConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("SourceType", out var sourceTypeElement))
        {
            throw new JsonException("SourceType property is required");
        }

        var sourceType = sourceTypeElement.GetString();
        var json = root.GetRawText();

        return sourceType switch
        {
            "WindowsEventSource" => JsonSerializer.Deserialize<WindowsEventSourceConfig>(json, options)!,
            "GitEventSource" => JsonSerializer.Deserialize<GitEventSourceConfig>(json, options)!,
            "OutlookEventSource" => JsonSerializer.Deserialize<OutlookEventSourceConfig>(json, options)!,
            "EdgeEventSource" => JsonSerializer.Deserialize<EdgeEventSourceConfig>(json, options)!,
            _ => throw new JsonException($"Unknown SourceType: {sourceType}")
        };
    }

    public override void Write(Utf8JsonWriter writer, EventSourceConfig value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
