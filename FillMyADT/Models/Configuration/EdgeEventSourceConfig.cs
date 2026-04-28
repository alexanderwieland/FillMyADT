using System.IO;

namespace FillMyADT.Models.Configuration;

/// <summary>
/// Configuration for Edge Browser History Event Source
/// </summary>
public record EdgeEventSourceConfig : EventSourceConfig
{
    public EdgeEventSourceConfig() : base()
    {
        SourceType = "EdgeEventSource";
    }

    /// <summary>
    /// Path to Edge profile directory (default: Default profile)
    /// </summary>
    public string ProfilePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Edge\User Data\Default");

    /// <summary>
    /// Only include URLs matching these domains (empty = all domains)
    /// </summary>
    public List<string> IncludeDomains { get; init; } = ["redmine.mp2.at", "tfs.mp2.at"];

    /// <summary>
    /// Exclude URLs matching these domains
    /// </summary>
    public List<string> ExcludeDomains { get; init; } = [];

    /// <summary>
    /// Minimum visit duration in seconds to include (filters out quick page loads)
    /// </summary>
    public int MinVisitDurationSeconds { get; init; } = 10;

    /// <summary>
    /// Maximum number of visits to retrieve per query (performance limit)
    /// </summary>
    public int MaxVisitsPerQuery { get; init; } = 1000;

    /// <summary>
    /// Maximum gap in minutes between TFS visits to consider them consecutive
    /// If gap > this value, use fixed 30min duration instead of time range
    /// </summary>
    public int TfsMaxGapMinutes { get; init; } = 60;

    /// <summary>
    /// Fixed duration in minutes for TFS tickets when visits are not consecutive
    /// </summary>
    public int TfsFixedDurationMinutes { get; init; } = 30;

    /// <summary>
    /// Include page title in event description
    /// </summary>
    public bool IncludePageTitle { get; init; } = true;
}
