namespace FillMyADT.Models.Configuration;

/// <summary>
/// Configuration for Windows Event Source
/// </summary>
public record WindowsEventSourceConfig : EventSourceConfig
{
    public WindowsEventSourceConfig() : base()
    {
        SourceType = "WindowsEventSource";
    }

    public WindowsEventSourceConfig(string sourceType) : base()
    {
        SourceType = sourceType;
    }

    /// <summary>
    /// Event log names to query (default: System)
    /// </summary>
    public List<string> EventLogNames { get; init; } = ["System"];

    /// <summary>
    /// Specific event IDs to include (empty means all relevant events)
    /// </summary>
    public List<long> IncludeEventIds { get; init; } = [];

    /// <summary>
    /// Event sources to include (e.g., "Power", "Kernel")
    /// </summary>
    public List<string> IncludeSources { get; init; } = ["Power", "Kernel", "EventLog"];
}
