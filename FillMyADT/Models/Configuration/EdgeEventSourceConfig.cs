using System.IO;

namespace FillMyADT.Models.Configuration;

/// <summary>
/// Configuration for Edge Browser History Event Source
/// </summary>
public record EdgeEventSourceConfig : EventSourceConfig
{
    /// <summary>
    /// Path to Edge profile directory (default: Default profile)
    /// </summary>
    public string ProfilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Edge\User Data\Default");

    /// <summary>
    /// Only include URLs matching these domains (empty = all domains)
    /// </summary>
    public List<string> IncludeDomains { get; set; } = ["redmine.mp2.at", "tfs.mp2.at"];

    /// <summary>
    /// Exclude URLs matching these domains
    /// </summary>
    public List<string> ExcludeDomains { get; set; } = [];

    /// <summary>
    /// Minimum visit duration in seconds to include (filters out quick page loads)
    /// </summary>
    public int MinVisitDurationSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum number of visits to retrieve per query (performance limit)
    /// </summary>
    public int MaxVisitsPerQuery { get; set; } = 1000;

    /// <summary>
    /// Minimum number of distinct 15-minute blocks a TFS ticket must be visited in per day.
    /// Tickets below this are treated as glances and ignored.
    /// </summary>
    public int TfsMinBlocks { get; set; } = 2;

    /// <summary>
    /// Include page title in event description
    /// </summary>
    public bool IncludePageTitle { get; set; } = true;
}
