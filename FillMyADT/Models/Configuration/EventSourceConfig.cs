namespace FillMyADT.Models.Configuration;

/// <summary>
/// Base configuration for event sources
/// </summary>
public abstract record EventSourceConfig
{
    /// <summary>
    /// Type name of the event source (for serialization)
    /// </summary>
    public string SourceType { get; init; } = string.Empty;

    /// <summary>
    /// Whether this event source is enabled
    /// </summary>
    public bool IsEnabled { get; init; } = true;
}
