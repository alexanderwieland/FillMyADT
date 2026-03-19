using System.Text.Json.Serialization;

namespace FillMyADT.Models.Configuration;

/// <summary>
/// Base configuration for event sources
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "SourceType")]
[JsonDerivedType(typeof(WindowsEventSourceConfig), typeDiscriminator: "WindowsEventSource")]
[JsonDerivedType(typeof(GitEventSourceConfig), typeDiscriminator: "GitEventSource")]
[JsonDerivedType(typeof(OutlookEventSourceConfig), typeDiscriminator: "OutlookEventSource")]
public abstract record EventSourceConfig
{
    /// <summary>
    /// Whether this event source is enabled
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Type name of the event source (for serialization) - managed by JsonPolymorphic
    /// </summary>
    [JsonIgnore]
    public string SourceType { get; init; } = string.Empty;
}
