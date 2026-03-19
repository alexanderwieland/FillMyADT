namespace FillMyADT.Models;

/// <summary>
/// Represents a single event from any source
/// </summary>
public record Event
{
    /// <summary>
    /// Unique identifier for the event
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Name of the event source (e.g., "Windows Events", "Git History")
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Event timestamp
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Type or category of the event (e.g., "Startup", "Shutdown", "Commit")
    /// </summary>
    public required string EventType { get; init; }

    /// <summary>
    /// Detailed description of the event
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Additional metadata specific to the event source
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }
}
