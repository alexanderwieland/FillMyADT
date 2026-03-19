using FillMyADT.Models;
using FillMyADT.Models.Configuration;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace FillMyADT.Services.EventSources;

/// <summary>
/// Event source that reads Git repository history (commits, branch switches)
/// </summary>
public class GitEventSource : IEventSource
{
    private readonly GitEventSourceConfig _config;
    private readonly List<string> _repositoryPaths;
    private static readonly Regex _reflogPattern = new(@"(?<ref>[^\s]+)\s+HEAD@{[^}]+}:\s+(?<action>.+?):\s+(?<date>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})", RegexOptions.Compiled);
    private readonly Regex _ticketPattern;

    // Performance constants
    private const int MaxCommitsPerRepo = 100;  // Limit commits per repo
    private const int MaxReflogEntries = 50;    // Limit reflog entries
    private const int GitCommandTimeoutSeconds = 10;  // Timeout for git commands

    public string Name => "Git History";

    public GitEventSource(GitEventSourceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _ticketPattern = new Regex(_config.TicketPattern, RegexOptions.Compiled);

        _repositoryPaths = [];

        if (config.RepositoryPaths.Count > 0)
        {
            _repositoryPaths.AddRange(config.RepositoryPaths);
        }

        if (config.AutoDiscoverRepositories)
        {
            if (!string.IsNullOrWhiteSpace(config.ScanDirectory))
            {
                var discovered = FindGitRepositories(config.ScanDirectory);
                foreach (var repo in discovered)
                {
                    if (!_repositoryPaths.Contains(repo))
                    {
                        _repositoryPaths.Add(repo);
                    }
                }
            }
            else
            {
                var discovered = FindGitRepository();
                if (discovered != null && !_repositoryPaths.Contains(discovered))
                {
                    _repositoryPaths.Add(discovered);
                }
            }
        }

        if (_repositoryPaths.Count == 0)
        {
            Log.Warning("GitEventSource: initialized with NO repositories - source will not be available");
        }
        else
        {
            Log.Information("GitEventSource: initialized with {Count} repositories", _repositoryPaths.Count);
        }
    }

    public async Task<IEnumerable<Event>> GetEventsAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
    {
        if (!_config.IsEnabled)
        {
            Log.Debug("GitEventSource is disabled");
            return [];
        }

        var stopwatch = Stopwatch.StartNew();
        var reposToScan = _repositoryPaths;

        if (_config.FilterByRecentActivity)
        {
            var activeRepos = new List<string>();
            foreach (var repoPath in _repositoryPaths)
            {
                var repoName = Path.GetFileName(repoPath);
                if (await HasRecentActivityAsync(repoPath, startDate, endDate, cancellationToken))
                {
                    activeRepos.Add(repoPath);
                }
            }
            reposToScan = activeRepos;
            Log.Information("GitEventSource: Scanning {ActiveCount} of {TotalCount} repositories with recent activity",
                activeRepos.Count, _repositoryPaths.Count);
        }

        if (reposToScan.Count == 0)
        {
            Log.Information("GitEventSource: No repositories with activity in date range");
            return [];
        }

        var allEvents = new List<Event>();

        foreach (var repoPath in reposToScan)
        {
            var repoName = Path.GetFileName(repoPath);
            try
            {
                var events = await GetEventsFromRepositoryAsync(repoPath, startDate, endDate, cancellationToken);
                var eventList = events.ToList();
                allEvents.AddRange(eventList);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GitEventSource: error reading Git events from repository {RepoName} ({Path})", repoName, repoPath);
            }
        }

        stopwatch.Stop();
        Log.Information("GitEventSource: Found {EventCount} events from {RepoCount} repositories in {ElapsedMs}ms",
            allEvents.Count, reposToScan.Count, stopwatch.ElapsedMilliseconds);

        return allEvents.OrderBy(e => e.Timestamp);
    }

    private async Task<IEnumerable<Event>> GetEventsFromRepositoryAsync(string repoPath, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
    {
        var events = new List<Event>();

        if (_config.IncludeCommits)
        {
            var commits = await GetCommitsAsync(repoPath, startDate, endDate, cancellationToken);
            events.AddRange(commits);
        }

        if (_config.IncludeBranchSwitches)
        {
            var reflogEvents = await GetReflogEventsAsync(repoPath, startDate, endDate, cancellationToken);
            events.AddRange(reflogEvents);
        }

        return events;
    }

    private async Task<IEnumerable<Event>> GetCommitsAsync(string repoPath, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
    {
        var events = new List<Event>();
        var repoName = Path.GetFileName(repoPath);

        try
        {
            var sinceArg = startDate.ToString("yyyy-MM-dd HH:mm:ss");
            var untilArg = endDate.ToString("yyyy-MM-dd HH:mm:ss");

            // OPTIMIZATION: Use HEAD instead of --all for faster queries
            var branchesArg = _config.IncludeBranches.Count > 0
                ? string.Join(" ", _config.IncludeBranches)
                : "HEAD";  // Changed from --all to HEAD for performance

            // OPTIMIZATION: Add limit to prevent fetching thousands of commits
            var output = await RunGitCommandAsync(
                repoPath,
                $"log {branchesArg} --since=\"{sinceArg}\" --until=\"{untilArg}\" --format=\"%H|%aI|%s|%an|%ae|%D\" -n {MaxCommitsPerRepo}",
                cancellationToken);

            if (string.IsNullOrWhiteSpace(output))
                return events;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var parts = line.Split('|');
                if (parts.Length >= 5 && DateTime.TryParse(parts[1], out var timestamp))
                {
                    var author = parts[3];
                    var email = parts[4];

                    if (!string.IsNullOrWhiteSpace(_config.FilterByAuthorName) &&
                        !author.Contains(_config.FilterByAuthorName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(_config.FilterByAuthorEmail) &&
                        !email.Contains(_config.FilterByAuthorEmail, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var commitMessage = parts[2];
                    var branchRefs = parts.Length > 5 ? parts[5] : string.Empty;

                    var metadata = new Dictionary<string, string>
                    {
                        ["Repository"] = repoName
                    };

                    var ticketNumber = ExtractTicketNumber(commitMessage);
                    if (string.IsNullOrEmpty(ticketNumber) && !string.IsNullOrWhiteSpace(branchRefs))
                    {
                        ticketNumber = ExtractTicketNumber(branchRefs);
                    }

                    if (!string.IsNullOrEmpty(ticketNumber))
                    {
                        metadata["TicketNumber"] = ticketNumber;
                    }

                    if (!string.IsNullOrWhiteSpace(branchRefs))
                    {
                        var branchName = ExtractBranchName(branchRefs);
                        if (!string.IsNullOrEmpty(branchName))
                        {
                            metadata["Branch"] = branchName;
                        }
                    }

                    var displayMessage = commitMessage.Length < 7 ? "Umsetzung" : commitMessage;

                    events.Add(new Event
                    {
                        Source = $"{Name} - {repoName}",
                        Timestamp = timestamp,
                        EventType = "Commit",
                        Description = displayMessage,
                        Metadata = metadata
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GitEventSource: error reading Git commits from {RepoName} ({Path})", repoName, repoPath);
        }

        return events;
    }

    private async Task<IEnumerable<Event>> GetReflogEventsAsync(string repoPath, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
    {
        var events = new List<Event>();
        var repoName = Path.GetFileName(repoPath);

        try
        {
            // CRITICAL OPTIMIZATION: Add limit to reflog command to prevent fetching thousands of entries
            var output = await RunGitCommandAsync(
                repoPath,
                $"reflog --date=iso -n {MaxReflogEntries}",
                cancellationToken);

            if (string.IsNullOrWhiteSpace(output))
                return events;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var match = _reflogPattern.Match(line);
                if (match.Success && DateTime.TryParse(match.Groups["date"].Value, out var timestamp))
                {
                    // OPTIMIZATION: Break early if we're past the date range (reflog is chronological)
                    if (timestamp < startDate)
                        break;

                    if (timestamp >= startDate && timestamp <= endDate)
                    {
                        var action = match.Groups["action"].Value;
                        if (action.Contains("checkout", StringComparison.OrdinalIgnoreCase))
                        {
                            var metadata = new Dictionary<string, string>
                            {
                                ["Repository"] = repoName
                            };

                            var ticketNumber = ExtractTicketNumber(action);
                            if (!string.IsNullOrEmpty(ticketNumber))
                            {
                                metadata["TicketNumber"] = ticketNumber;
                            }

                            var branchName = ExtractBranchNameFromCheckout(action);
                            if (!string.IsNullOrEmpty(branchName))
                            {
                                metadata["Branch"] = branchName;
                            }

                            events.Add(new Event
                            {
                                Source = $"{Name} - {repoName}",
                                Timestamp = timestamp,
                                EventType = "Branch Switch",
                                Description = action,
                                Metadata = metadata
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GitEventSource: error reading Git reflog from {RepoName} ({Path})", repoName, repoPath);
        }

        return events;
    }

    private static async Task<string> RunGitCommandAsync(string workingDirectory, string arguments, CancellationToken cancellationToken)
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processInfo);
        if (process == null)
            return string.Empty;

        // OPTIMIZATION: Add timeout to prevent hanging
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(GitCommandTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);

            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            Log.Warning("Git command timed out after {Seconds}s: git {Arguments}", GitCommandTimeoutSeconds, arguments);
            try { process.Kill(); } catch { }
            return string.Empty;
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.IsEnabled)
        {
            Log.Debug("GitEventSource not available: disabled in configuration");
            return false;
        }

        if (_repositoryPaths.Count == 0)
        {
            Log.Warning("GitEventSource not available: no repositories configured or discovered");
            return false;
        }

        try
        {
            foreach (var repoPath in _repositoryPaths)
            {
                var output = await RunGitCommandAsync(repoPath, "rev-parse --git-dir", cancellationToken);
                if (!string.IsNullOrWhiteSpace(output))
                {
                    return true;
                }
            }
            var repoNames = _repositoryPaths.Select(p => System.IO.Path.GetFileName(p));
            Log.Warning("GitEventSource not available: none of the {Count} repositories are valid Git repos", _repositoryPaths.Count);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Git not available - error checking repositories");
            return false;
        }
    }

    private static string? FindGitRepository()
    {
        var currentDir = Environment.CurrentDirectory;

        while (!string.IsNullOrEmpty(currentDir))
        {
            var gitDir = System.IO.Path.Combine(currentDir, ".git");
            if (System.IO.Directory.Exists(gitDir))
                return currentDir;

            var parent = System.IO.Directory.GetParent(currentDir);
            currentDir = parent?.FullName ?? string.Empty;
        }

        return null;
    }

    private static List<string> FindGitRepositories(string scanDirectory)
    {
        var repositories = new List<string>();

        if (!System.IO.Directory.Exists(scanDirectory))
        {
            Log.Warning("Scan directory does not exist: {Path}", scanDirectory);
            return repositories;
        }

        try
        {
            var directories = System.IO.Directory.GetDirectories(scanDirectory, "*", System.IO.SearchOption.TopDirectoryOnly);

            foreach (var dir in directories)
            {
                var gitDir = System.IO.Path.Combine(dir, ".git");
                if (System.IO.Directory.Exists(gitDir))
                {
                    repositories.Add(dir);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error scanning directory for repositories: {Path}", scanDirectory);
        }

        return repositories;
    }

    private async Task<bool> HasRecentActivityAsync(string repoPath, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
    {
        var repoName = Path.GetFileName(repoPath);
        try
        {
            // OPTIMIZATION: Check files first (very fast!)
            if (_config.UseFetchHeadFilter)
            {
                var gitDir = Path.Combine(repoPath, ".git");
                var filesToCheck = new[] { "FETCH_HEAD", "HEAD", "index" };

                foreach (var fileName in filesToCheck)
                {
                    var filePath = Path.Combine(gitDir, fileName);
                    if (File.Exists(filePath))
                    {
                        var lastModified = File.GetLastWriteTime(filePath);
                        if (lastModified >= startDate && lastModified <= endDate)
                        {
                            return true;
                        }
                    }
                }
            }

            // OPTIMIZATION: Use HEAD instead of --all for faster activity check
            var sinceArg = startDate.ToString("yyyy-MM-dd HH:mm:ss");
            var untilArg = endDate.ToString("yyyy-MM-dd HH:mm:ss");

            var output = await RunGitCommandAsync(
                repoPath,
                $"log HEAD --since=\"{sinceArg}\" --until=\"{untilArg}\" --format=\"%H\" -n 1",
                cancellationToken);

            return !string.IsNullOrWhiteSpace(output);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GitEventSource: error checking activity for repository {RepoName} ({Path})", repoName, repoPath);
            return false;
        }
    }

    internal string? ExtractTicketNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = _ticketPattern.Match(text);
        if (match.Success && match.Groups.Count > 1)
        {
            return match.Groups[1].Value;
        }

        return null;
    }

    private static string? ExtractBranchName(string branchRefs)
    {
        if (string.IsNullOrWhiteSpace(branchRefs))
            return null;

        var headMatch = Regex.Match(branchRefs, @"HEAD -> ([^,]+)");
        if (headMatch.Success)
        {
            return headMatch.Groups[1].Value.Trim();
        }

        var parts = branchRefs.Split(',', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0].Trim() : null;
    }

    private static string? ExtractBranchNameFromCheckout(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return null;

        var match = Regex.Match(action, @"checkout:\s+moving from .+ to (.+)");
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        return null;
    }

    /// <summary>
    /// Get the current branch name for the primary repository
    /// </summary>
    public async Task<string?> GetCurrentBranchAsync(CancellationToken cancellationToken = default)
    {
        if (_repositoryPaths.Count == 0)
            return null;

        try
        {
            var output = await RunGitCommandAsync(_repositoryPaths[0], "rev-parse --abbrev-ref HEAD", cancellationToken);
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error getting current branch");
            return null;
        }
    }

    /// <summary>
    /// Get the ticket number from the current branch name
    /// </summary>
    public async Task<string?> GetCurrentBranchTicketNumberAsync(CancellationToken cancellationToken = default)
    {
        var branchName = await GetCurrentBranchAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(branchName))
            return null;

        var ticketNumber = ExtractTicketNumber(branchName);
        Log.Debug("Current branch: {Branch}, Ticket: {Ticket}", branchName, ticketNumber ?? "none");
        return ticketNumber;
    }

    /// <summary>
    /// Get the branch that was active at a specific time by examining reflog history
    /// </summary>
    public async Task<string?> GetBranchAtTimeAsync(DateTime time, CancellationToken cancellationToken = default)
    {
        if (_repositoryPaths.Count == 0)
            return null;

        try
        {
            // OPTIMIZATION: Limit reflog entries for historical queries
            var output = await RunGitCommandAsync(_repositoryPaths[0], $"reflog --date=iso -n {MaxReflogEntries}", cancellationToken);

            if (string.IsNullOrWhiteSpace(output))
                return await GetCurrentBranchAsync(cancellationToken);

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var match = _reflogPattern.Match(line);
                if (match.Success && DateTime.TryParse(match.Groups["date"].Value, out var entryTime))
                {
                    if (entryTime <= time)
                    {
                        var action = match.Groups["action"].Value;
                        if (action.Contains("checkout", StringComparison.OrdinalIgnoreCase))
                        {
                            var branchName = ExtractBranchNameFromCheckout(action);
                            if (!string.IsNullOrEmpty(branchName))
                            {
                                Log.Debug("Branch at {Time}: {Branch} (from reflog entry at {EntryTime})",
                                    time, branchName, entryTime);
                                return branchName;
                            }
                        }
                    }
                }
            }

            return await GetCurrentBranchAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error getting branch at time {Time}", time);
            return await GetCurrentBranchAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Get the ticket number from the branch that was active at a specific time
    /// </summary>
    public async Task<string?> GetBranchTicketNumberAtTimeAsync(DateTime time, CancellationToken cancellationToken = default)
    {
        var branchName = await GetBranchAtTimeAsync(time, cancellationToken);
        if (string.IsNullOrWhiteSpace(branchName))
            return null;

        var ticketNumber = ExtractTicketNumber(branchName);
        Log.Debug("Branch at {Time}: {Branch}, Ticket: {Ticket}", time, branchName, ticketNumber ?? "none");
        return ticketNumber;
    }
}
