namespace FillMyADT.Models.Configuration;

/// <summary>
/// Base configuration for event sources
/// </summary>
public abstract record EventSourceConfig
{
    /// <summary>
    /// Whether this event source is enabled
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}
