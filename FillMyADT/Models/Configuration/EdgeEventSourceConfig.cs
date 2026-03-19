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
    /// URL patterns for extracting ticket numbers
    /// Format: (Name, UrlRegexPattern, TicketExtractionPattern)
    /// </summary>
    public List<UrlTicketPattern> TicketPatterns { get; init; } =
    [
        // Redmine: https://redmine.mp2.at/issues/12345
        new("Redmine", @"redmine\.mp2\.at/issues/(\d+)", "$1"),
        
        // TFS Pull Requests: https://tfs.mp2.at/DefaultCollection/_git/ProjectName/pullrequest/123
        new("TFS-PR", @"tfs\.mp2\.at/.+/pullrequest/(\d+)", "PR-$1"),
        
        // TFS Work Items: https://tfs.mp2.at/DefaultCollection/_workitems/edit/12345
        new("TFS-WI", @"tfs\.mp2\.at/.+/_workitems/edit/(\d+)", "$1"),
        
        // TFS Work Items (alternative): https://tfs.mp2.at/DefaultCollection/_workitems?id=12345
        new("TFS-WI-Alt", @"tfs\.mp2\.at/.+/_workitems\?id=(\d+)", "$1")
    ];

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
    public int MinVisitDurationSeconds { get; init; } = 5;

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

/// <summary>
/// Represents a URL pattern for extracting ticket numbers
/// </summary>
/// <param name="Name">Name of the pattern (e.g., "Redmine", "TFS-PR")</param>
/// <param name="UrlPattern">Regex pattern to match URLs</param>
/// <param name="TicketFormat">Format string for ticket number ($1 = first capture group)</param>
public record UrlTicketPattern(string Name, string UrlPattern, string TicketFormat);
