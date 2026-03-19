namespace FillMyADT.Models.Configuration;

/// <summary>
/// Configuration for Git Event Source
/// </summary>
public record GitEventSourceConfig : EventSourceConfig
{
    public GitEventSourceConfig() : base()
    {
        SourceType = "GitEventSource";
    }

    public GitEventSourceConfig(string sourceType) : base()
    {
        SourceType = sourceType;
    }

    /// <summary>
    /// List of Git repository paths to monitor
    /// </summary>
    public List<string> RepositoryPaths { get; init; } = [];

    /// <summary>
    /// Whether to automatically discover Git repositories in parent directories
    /// </summary>
    public bool AutoDiscoverRepositories { get; init; } = true;

    /// <summary>
    /// Base directory to scan for Git repositories (e.g., C:\Work\Sync\Git\)
    /// </summary>
    public string? ScanDirectory { get; init; }

    /// <summary>
    /// Only include repositories with activity within the query date range
    /// </summary>
    public bool FilterByRecentActivity { get; init; } = true;

    /// <summary>
    /// Use FETCH_HEAD file timestamp for quick activity check (faster than git log)
    /// </summary>
    public bool UseFetchHeadFilter { get; init; } = true;

    /// <summary>
    /// Regex pattern to extract ticket numbers from commits and branches (default: #\d+)
    /// </summary>
    public string TicketPattern { get; init; } = @"#(\d+)";

    /// <summary>
    /// Filter commits by author name (empty means all authors)
    /// </summary>
    public string? FilterByAuthorName { get; init; }

    /// <summary>
    /// Filter commits by author email (empty means all authors)
    /// </summary>
    public string? FilterByAuthorEmail { get; init; }

    /// <summary>
    /// Include commit events
    /// </summary>
    public bool IncludeCommits { get; init; } = true;

    /// <summary>
    /// Include branch switch events
    /// </summary>
    public bool IncludeBranchSwitches { get; init; } = true;

    /// <summary>
    /// Branches to include (empty means all branches)
    /// </summary>
    public List<string> IncludeBranches { get; init; } = [];
}
