namespace FillMyADT.Models.Configuration;

/// <summary>
/// Configuration for Git Event Source
/// </summary>
public record GitEventSourceConfig : EventSourceConfig
{
    /// <summary>
    /// Path to the default/primary Git repository used to resolve the active branch for a given day.
    /// When set, filler time slots (gaps without Git events) get the ticket number from the branch
    /// that was checked out in this repository on that day.
    /// </summary>
    public string? DefaultRepositoryPath { get; set; } = "MP2.Gast";

    /// <summary>
    /// List of Git repository paths to monitor
    /// </summary>
    public List<string> RepositoryPaths { get; set; } = [];

    /// <summary>
    /// Whether to automatically discover Git repositories in parent directories
    /// </summary>
    public bool AutoDiscoverRepositories { get; set; } = true;

    /// <summary>
    /// Base directory to scan for Git repositories (e.g., C:\Work\Sync\Git\)
    /// </summary>
    public string? ScanDirectory { get; set; }

    /// <summary>
    /// Only include repositories with activity within the query date range
    /// </summary>
    public bool FilterByRecentActivity { get; set; } = true;

    /// <summary>
    /// Use FETCH_HEAD file timestamp for quick activity check (faster than git log)
    /// </summary>
    public bool UseFetchHeadFilter { get; set; } = true;

    /// <summary>
    /// Filter commits by author name (empty means all authors)
    /// </summary>
    public string? FilterByAuthorName { get; set; }

    /// <summary>
    /// Filter commits by author email (empty means all authors)
    /// </summary>
    public string? FilterByAuthorEmail { get; set; }

    /// <summary>
    /// Include commit events
    /// </summary>
    public bool IncludeCommits { get; set; } = true;

    /// <summary>
    /// Include branch switch events
    /// </summary>
    public bool IncludeBranchSwitches { get; set; } = true;

    /// <summary>
    /// Branches to include (empty means all branches)
    /// </summary>
    public List<string> IncludeBranches { get; set; } = [];
}
