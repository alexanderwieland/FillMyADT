namespace FillMyADT.Models.Configuration;

/// <summary>
/// Configuration for Windows Event Source
/// </summary>
public record WindowsEventSourceConfig : EventSourceConfig
{
    public WindowsEventSourceConfig() => SourceType = "WindowsEventSource";
}
