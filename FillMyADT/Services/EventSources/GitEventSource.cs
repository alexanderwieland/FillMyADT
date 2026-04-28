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
    private static readonly ILogger Log = Serilog.Log.ForContext<GitEventSource>();

    private readonly GitEventSourceConfig _config;
    private readonly IReadOnlyList<string> _repositoryPaths;

    /// <summary>
    /// The primary repository used for branch lookups (DefaultRepositoryPath if set, else first discovered repo).
    /// </summary>
    private readonly string? _defaultRepositoryPath;
    // Simplified regex to extract date from HEAD@{date} format
    private static readonly Regex _reflogDatePattern = new(@"HEAD@\{(.+?)\}", RegexOptions.Compiled);
    private static readonly Regex _branchHeadPattern = new(@"HEAD -> ([^,]+)", RegexOptions.Compiled);
    private static readonly Regex _checkoutPattern = new(@"checkout:\s+moving from .+ to (.+)", RegexOptions.Compiled);

    // Performance constants
    private const int MaxCommitsPerRepo = 50;   // Limit commits per repo (reduced from 100)
    private const int MaxReflogEntries = 20;     // Limit reflog entries for event scanning (recent activity only)
    private const int MaxBranchLookupEntries = 500; // Higher limit for past-day branch lookup via reflog
    private const int GitCommandTimeoutSeconds = 5;  // Timeout for git commands (reduced from 10)

    private const string DefaultCommitDescription = "Umsetzung";
    private const string GitDateFormat = "yyyy-MM-dd HH:mm:ss";

    public string Name => "Git History";

    public GitEventSource(GitEventSourceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;

        var paths = new List<string>();

        if (config.RepositoryPaths.Count > 0)
            paths.AddRange(config.RepositoryPaths);

        if (config.AutoDiscoverRepositories)
        {
            if (!string.IsNullOrWhiteSpace(config.ScanDirectory))
            {
                foreach (var repo in FindGitRepositories(config.ScanDirectory))
                {
                    if (!paths.Contains(repo))
                        paths.Add(repo);
                }
            }
            else
            {
                var discovered = FindGitRepository();
                if (discovered != null && !paths.Contains(discovered))
                    paths.Add(discovered);
            }
        }

        _repositoryPaths = paths;

        // Resolve the default/primary repository for branch lookups.
        // The configured value may be a full path OR just a folder name relative to ScanDirectory.
        var resolvedDefault = ResolveDefaultRepositoryPath(config.DefaultRepositoryPath, config.ScanDirectory);

        if (!string.IsNullOrWhiteSpace(resolvedDefault))
        {
            _defaultRepositoryPath = resolvedDefault;
            Log.Information("GitEventSource: default repository for branch lookups is '{Name}' ({Path})",
                Path.GetFileName(_defaultRepositoryPath), _defaultRepositoryPath);

            // Make sure the default repo is also scanned for events
            if (!paths.Contains(_defaultRepositoryPath))
                paths.Add(_defaultRepositoryPath);
        }
        else
        {
            _defaultRepositoryPath = paths.Count > 0 ? paths[0] : null;
            if (_defaultRepositoryPath != null)
                Log.Information("GitEventSource: no DefaultRepositoryPath configured or resolved; " +
                    "falling back to first repo '{Name}' for branch lookups", Path.GetFileName(_defaultRepositoryPath));
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
            // OPTIMIZATION: Check repos in parallel (much faster for many repos!)
            var activityTasks = _repositoryPaths.Select(async repoPath =>
            {
                var hasActivity = await HasRecentActivityAsync(repoPath, startDate, endDate, cancellationToken);
                return (repoPath, hasActivity);
            });

            var results = await Task.WhenAll(activityTasks);
            var activeRepos = results.Where(r => r.hasActivity).Select(r => r.repoPath).ToList();

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
                allEvents.AddRange(events);
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
        var repoName = Path.GetFileName(repoPath);
        var sw = Stopwatch.StartNew();

        var tasks = new List<Task<IEnumerable<Event>>>();
        if (_config.IncludeCommits) tasks.Add(GetCommitsAsync(repoPath, startDate, endDate, cancellationToken));
        if (_config.IncludeBranchSwitches) tasks.Add(GetReflogEventsAsync(repoPath, startDate, endDate, cancellationToken));

        foreach (var result in await Task.WhenAll(tasks))
            events.AddRange(result);

        sw.Stop();
        Log.Debug("GitEventSource: {RepoName} processed in {ElapsedMs}ms ({EventCount} events)",
            repoName, sw.ElapsedMilliseconds, events.Count);

        return events;
    }

    private async Task<IEnumerable<Event>> GetCommitsAsync(string repoPath, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
    {
        var events = new List<Event>();
        var repoName = Path.GetFileName(repoPath);

        try
        {
            var sinceArg = startDate.ToString(GitDateFormat);
            var untilArg = endDate.ToString(GitDateFormat);

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

                    var ticketNumber = TicketParser.FromGitText(commitMessage);
                    if (string.IsNullOrEmpty(ticketNumber) && !string.IsNullOrWhiteSpace(branchRefs))
                    {
                        ticketNumber = TicketParser.FromGitText(branchRefs);
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

                    var displayMessage = commitMessage.Length < 7 ? DefaultCommitDescription : commitMessage;

                    events.Add(new Event
                    {
                        Source = $"{Name} - {repoName}",
                        Timestamp = timestamp,
                        EventType = EventType.Commit,
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
            // OPTIMIZATION: Use structured format for easier parsing
            // Format: hash|HEAD@{date}|action
            var output = await RunGitCommandAsync(
                repoPath,
                $"reflog --format='%H|%gd|%gs' --date=iso -n {MaxReflogEntries}",
                cancellationToken);

            if (string.IsNullOrWhiteSpace(output))
                return events;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (!TryParseReflogLine(line, out var parsed))
                    continue;

                // Break early if we're past the date range (reflog is newest-first)
                if (parsed.Timestamp < startDate)
                    break;

                if (parsed.Timestamp > endDate)
                    continue;

                var reflogEvent = await DispatchReflogEntryAsync(parsed.Hash, parsed.Action, parsed.Timestamp, repoPath, repoName, cancellationToken);
                if (reflogEvent != null)
                    events.Add(reflogEvent);
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
            var gitDir = Path.Combine(currentDir, ".git");
            if (Directory.Exists(gitDir))
                return currentDir;

            var parent = Directory.GetParent(currentDir);
            currentDir = parent?.FullName ?? string.Empty;
        }

        return null;
    }

    /// <summary>
    /// Resolve the configured DefaultRepositoryPath to an absolute directory.
    /// Accepts: absolute path, or just a folder name / relative path resolved under ScanDirectory.
    /// Returns null if nothing could be resolved.
    /// </summary>
    private static string? ResolveDefaultRepositoryPath(string? configured, string? scanDirectory)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        // 1. Already a valid absolute path
        if (Path.IsPathRooted(configured) && Directory.Exists(configured))
            return configured;

        // 2. Try as a subfolder name / relative path under ScanDirectory
        if (!string.IsNullOrWhiteSpace(scanDirectory))
        {
            var candidate = Path.Combine(scanDirectory, configured);
            if (Directory.Exists(candidate))
                return candidate;
        }

        Log.Warning("GitEventSource: DefaultRepositoryPath '{Configured}' could not be resolved " +
            "(not an absolute path and not found under ScanDirectory '{ScanDir}')",
            configured, scanDirectory ?? "(not set)");
        return null;
    }

    private static List<string> FindGitRepositories(string scanDirectory)
    {
        var repositories = new List<string>();

        if (!Directory.Exists(scanDirectory))
        {
            Log.Warning("Scan directory does not exist: {Path}", scanDirectory);
            return repositories;
        }

        try
        {
            var directories = Directory.GetDirectories(scanDirectory, "*", SearchOption.TopDirectoryOnly);

            foreach (var dir in directories)
            {
                var gitDir = Path.Combine(dir, ".git");
                if (Directory.Exists(gitDir))
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
            var sinceArg = startDate.ToString(GitDateFormat);
            var untilArg = endDate.ToString(GitDateFormat);

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

    private static string? ExtractBranchName(string branchRefs)
    {
        if (string.IsNullOrWhiteSpace(branchRefs))
            return null;

        var headMatch = _branchHeadPattern.Match(branchRefs);
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

        var match = _checkoutPattern.Match(action);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        return null;
    }

    private readonly record struct ReflogEntry(string Hash, DateTime Timestamp, string Action);

    private static bool TryParseReflogLine(string line, out ReflogEntry entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var parts = line.Split('|');
        if (parts.Length < 3) return false;

        var dateMatch = _reflogDatePattern.Match(parts[1]);
        if (!dateMatch.Success || !DateTime.TryParse(dateMatch.Groups[1].Value, out var timestamp)) return false;

        entry = new ReflogEntry(parts[0], timestamp, parts[2]);
        return true;
    }

    /// <summary>
    /// Dispatch a parsed reflog entry to the appropriate event factory
    /// </summary>
    private async Task<Event?> DispatchReflogEntryAsync(string commitHash, string action, DateTime timestamp, string repoPath, string repoName, CancellationToken cancellationToken)
    {
        if (action.Contains("rebase", StringComparison.OrdinalIgnoreCase))
            return await HandleRebaseActionAsync(action, commitHash, timestamp, repoPath, repoName, cancellationToken);

        if (action.Contains("checkout", StringComparison.OrdinalIgnoreCase))
            return CreateCheckoutEvent(action, timestamp, repoName);

        if (action.StartsWith("commit", StringComparison.OrdinalIgnoreCase))
            return await CreateCommitEventFromReflogAsync(repoPath, commitHash, action, timestamp, repoName, cancellationToken);

        return null;
    }

    /// <summary>
    /// Create a BranchSwitch event from a checkout action
    /// </summary>
    private Event? CreateCheckoutEvent(string action, DateTime timestamp, string repoName)
    {
        var metadata = new Dictionary<string, string>
        {
            ["Repository"] = repoName
        };

        // Extract the destination branch name first
        var branchName = ExtractBranchNameFromCheckout(action);
        if (!string.IsNullOrEmpty(branchName))
        {
            metadata["Branch"] = branchName;

            // Extract ticket from the destination branch name, not from the entire action
            // (action contains both source and destination, we only want destination)
            var ticketNumber = TicketParser.FromGitText(branchName);
            if (!string.IsNullOrEmpty(ticketNumber))
            {
                metadata["TicketNumber"] = ticketNumber;
            }
        }

        return new Event
        {
            Source = $"{Name} - {repoName}",
            Timestamp = timestamp,
            EventType = EventType.BranchSwitch,
            Description = action,
            Metadata = metadata
        };
    }

    // Matches "rebase (start): checkout <hash>"
    private static readonly Regex _rebaseStartPattern =
        new(@"rebase \(start\):\s+checkout\s+([0-9a-f]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches "rebase (finish): returning to refs/heads/<branch>"
    private static readonly Regex _rebaseFinishPattern =
        new(@"rebase \(finish\):\s+returning to refs/heads/(.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Handle rebase-related reflog actions.
    /// Start: resolves the target commit hash to a subject and branch refs.
    /// Finish: extracts the branch that was rebased.
    /// All other rebase steps (pick, squash, amend, �) are skipped as noise.
    /// </summary>
    private async Task<Event?> HandleRebaseActionAsync(string action, string commitHash, DateTime timestamp, string repoPath, string repoName, CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>
        {
            ["Repository"] = repoName,
            ["RebaseAction"] = action
        };

        // rebase (start): checkout <hash>
        var startMatch = _rebaseStartPattern.Match(action);
        if (startMatch.Success)
        {
            var targetHash = startMatch.Groups[1].Value;
            var shortHash = targetHash[..Math.Min(7, targetHash.Length)];

            // Resolve hash ? subject + branch refs
            string description;
            try
            {
                var logOutput = await RunGitCommandAsync(
                    repoPath,
                    $"log -1 --format=%s|%D {targetHash}",
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(logOutput))
                {
                    var logParts = logOutput.Trim().Split('|');
                    var subject = logParts[0].Trim();
                    var refs = logParts.Length > 1 ? logParts[1].Trim() : string.Empty;

                    var branchName = ExtractBranchName(refs);
                    if (!string.IsNullOrEmpty(branchName))
                    {
                        metadata["Branch"] = branchName;
                        var ticket = TicketParser.FromGitText(branchName);
                        if (!string.IsNullOrEmpty(ticket))
                            metadata["TicketNumber"] = ticket;
                    }

                    description = string.IsNullOrWhiteSpace(subject)
                        ? $"Rebase started onto {shortHash}"
                        : $"Rebase onto {shortHash}: {subject}";
                }
                else
                {
                    description = $"Rebase started onto {shortHash}";
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not resolve rebase target hash {Hash}", targetHash);
                description = $"Rebase started onto {shortHash}";
            }

            return new Event
            {
                Source = $"{Name} - {repoName}",
                Timestamp = timestamp,
                EventType = EventType.BranchSwitch,
                Description = description,
                Metadata = metadata
            };
        }

        // rebase (finish): returning to refs/heads/<branch>
        var finishMatch = _rebaseFinishPattern.Match(action);
        if (finishMatch.Success)
        {
            var branchName = finishMatch.Groups[1].Value.Trim();
            metadata["Branch"] = branchName;

            var ticket = TicketParser.FromGitText(branchName);
            if (!string.IsNullOrEmpty(ticket))
                metadata["TicketNumber"] = ticket;

            return new Event
            {
                Source = $"{Name} - {repoName}",
                Timestamp = timestamp,
                EventType = EventType.BranchSwitch,
                Description = $"Rebase finished: {branchName}",
                Metadata = metadata
            };
        }

        // All other mid-rebase steps (pick, squash, amend, �) are noise � skip them
        Log.Debug("Skipping mid-rebase reflog entry: {Action}", action);
        return null;
    }

    /// <summary>
    /// Create a Commit event from reflog with branch information
    /// </summary>
    private async Task<Event?> CreateCommitEventFromReflogAsync(string repoPath, string commitHash, string action, DateTime timestamp, string repoName, CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>
        {
            ["Repository"] = repoName,
            ["CommitHash"] = commitHash
        };

        // Extract commit message from action (format: "commit: message" or "commit (amend): message")
        var commitMessage = ExtractCommitMessage(action);
        var ticketNumber = TicketParser.FromGitText(commitMessage);
        if (!string.IsNullOrEmpty(ticketNumber))
        {
            metadata["TicketNumber"] = ticketNumber;
        }

        // Get branch name from the commit refs
        try
        {
            var branchOutput = await RunGitCommandAsync(
                repoPath,
                $"log -1 --format='%D' {commitHash}",
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(branchOutput))
            {
                var branchName = ExtractBranchName(branchOutput);
                if (!string.IsNullOrEmpty(branchName))
                {
                    metadata["Branch"] = branchName;

                    // If no ticket in commit message, try to extract from branch name
                    if (string.IsNullOrEmpty(ticketNumber))
                    {
                        ticketNumber = TicketParser.FromGitText(branchName);
                        if (!string.IsNullOrEmpty(ticketNumber))
                        {
                            metadata["TicketNumber"] = ticketNumber;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not retrieve branch info for commit {Hash}", commitHash);
        }

        var displayMessage = string.IsNullOrWhiteSpace(commitMessage) || commitMessage.Length < 7
            ? DefaultCommitDescription
            : commitMessage;

        return new Event
        {
            Source = $"{Name} - {repoName}",
            Timestamp = timestamp,
            EventType = EventType.Commit,
            Description = displayMessage,
            Metadata = metadata
        };
    }

    /// <summary>
    /// Extract commit message from reflog action line
    /// </summary>
    private static string ExtractCommitMessage(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return string.Empty;

        // Format: "commit: message" or "commit (amend): message"
        var colonIndex = action.IndexOf(':');
        if (colonIndex > 0 && colonIndex < action.Length - 1)
        {
            return action.Substring(colonIndex + 1).Trim();
        }

        return action;
    }

    /// <summary>
    /// Get the current branch name for the default/primary repository
    /// </summary>
    public async Task<string?> GetCurrentBranchAsync(CancellationToken cancellationToken = default)
    {
        if (_defaultRepositoryPath == null)
            return null;

        try
        {
            var output = await RunGitCommandAsync(_defaultRepositoryPath, "rev-parse --abbrev-ref HEAD", cancellationToken);
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

        var ticketNumber = TicketParser.FromGitText(branchName);
        Log.Debug("Current branch: {Branch}, Ticket: {Ticket}", branchName, ticketNumber ?? "none");
        return ticketNumber;
    }

    /// <summary>
    /// Get the branch that was active at a specific time by examining the default repository's reflog history.
    /// Uses a date-scoped query so past days are found regardless of how many operations happened since.
    /// </summary>
    public async Task<string?> GetBranchAtTimeAsync(DateTime time, CancellationToken cancellationToken = default)
    {
        if (_defaultRepositoryPath == null)
        {
            Log.Warning("GetBranchAtTimeAsync: no default repository configured, cannot resolve branch for {Time}", time);
            return null;
        }

        Log.Information("GetBranchAtTimeAsync: looking up branch at {Time} in default repo '{Repo}'",
            time, Path.GetFileName(_defaultRepositoryPath));

        try
        {
            // Use git log -g with a date window so we are never limited by a fixed entry count.
            // We scan from start-of-day to time+1h to capture any checkout on that day.
            var after  = time.Date.AddDays(-1).ToString(GitDateFormat);  // day before as lower bound
            var before = time.AddHours(1).ToString(GitDateFormat);        // time + 1h as upper bound

            var output = await RunGitCommandAsync(
                _defaultRepositoryPath,
                $"log -g --format=%H|%gd|%gs --date=iso --after=\"{after}\" --before=\"{before}\" -n {MaxBranchLookupEntries}",
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(output))
            {
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Log.Debug("GetBranchAtTimeAsync: {Count} reflog entries in date window for '{Repo}'",
                    lines.Length, Path.GetFileName(_defaultRepositoryPath));

                // Entries are newest-first; find the last checkout at or before 'time'
                foreach (var line in lines)
                {
                    if (!TryParseReflogLine(line, out var parsed) || parsed.Timestamp > time)
                        continue;

                    if (parsed.Action.Contains("checkout", StringComparison.OrdinalIgnoreCase))
                    {
                        var branchName = ExtractBranchNameFromCheckout(parsed.Action);
                        if (!string.IsNullOrEmpty(branchName))
                        {
                            Log.Information("GetBranchAtTimeAsync: resolved branch '{Branch}' at {Time} (reflog entry at {EntryTime}) in '{Repo}'",
                                branchName, time, parsed.Timestamp, Path.GetFileName(_defaultRepositoryPath));
                            return branchName;
                        }
                    }
                }

                Log.Debug("GetBranchAtTimeAsync: no checkout entry found in date window, widening search");
            }

            // Fallback: scan a larger window (all reflog up to the target time)
            var wideOutput = await RunGitCommandAsync(
                _defaultRepositoryPath,
                $"log -g --format=%H|%gd|%gs --date=iso -n {MaxBranchLookupEntries}",
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(wideOutput))
            {
                var wideLines = wideOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Log.Debug("GetBranchAtTimeAsync: wide search � {Count} total reflog entries in '{Repo}'",
                    wideLines.Length, Path.GetFileName(_defaultRepositoryPath));

                foreach (var line in wideLines)
                {
                    if (!TryParseReflogLine(line, out var parsed) || parsed.Timestamp > time)
                        continue;

                    if (parsed.Action.Contains("checkout", StringComparison.OrdinalIgnoreCase))
                    {
                        var branchName = ExtractBranchNameFromCheckout(parsed.Action);
                        if (!string.IsNullOrEmpty(branchName))
                        {
                            Log.Information("GetBranchAtTimeAsync: resolved branch '{Branch}' at {Time} via wide search (reflog entry at {EntryTime}) in '{Repo}'",
                                branchName, time, parsed.Timestamp, Path.GetFileName(_defaultRepositoryPath));
                            return branchName;
                        }
                    }
                }
            }

            Log.Warning("GetBranchAtTimeAsync: no checkout found in reflog of '{Repo}' for {Time}; falling back to current branch",
                Path.GetFileName(_defaultRepositoryPath), time);
            return await GetCurrentBranchAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GetBranchAtTimeAsync: error querying reflog of '{Repo}' for {Time}",
                Path.GetFileName(_defaultRepositoryPath), time);
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

        var ticketNumber = TicketParser.FromGitText(branchName);
        Log.Debug("Branch at {Time}: {Branch}, Ticket: {Ticket}", time, branchName, ticketNumber ?? "none");
        return ticketNumber;
    }

    /// <summary>
    /// Scan every reflog entry for a given calendar day (commits, rebases, checkouts) and return
    /// the first ticket number found in any action message or branch name.
    /// This is the fallback when the checked-out branch name itself carries no ticket number.
    /// </summary>
    public async Task<string?> GetTicketFromDayReflogAsync(DateTime day, CancellationToken cancellationToken = default)
    {
        if (_defaultRepositoryPath == null)
            return null;

        var after  = day.Date.AddDays(-1).ToString(GitDateFormat);
        var before = day.Date.AddDays(1).ToString(GitDateFormat);

        try
        {
            var output = await RunGitCommandAsync(
                _defaultRepositoryPath,
                $"log -g --format=%H|%gd|%gs --date=iso --after=\"{after}\" --before=\"{before}\" -n {MaxBranchLookupEntries}",
                cancellationToken);

            if (string.IsNullOrWhiteSpace(output))
                return null;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Log.Debug("GetTicketFromDayReflogAsync: {Count} reflog entries on {Date} in '{Repo}'",
                lines.Length, day.Date.ToShortDateString(), Path.GetFileName(_defaultRepositoryPath));

            foreach (var line in lines)
            {
                if (!TryParseReflogLine(line, out var parsed))
                    continue;

                // Skip pure rebase noise lines (mid-step entries with no useful message)
                if (parsed.Action.Equals("rebase", StringComparison.OrdinalIgnoreCase))
                    continue;

                var ticket = TicketParser.FromGitText(parsed.Action);
                if (!string.IsNullOrEmpty(ticket))
                {
                    Log.Information("GetTicketFromDayReflogAsync: found ticket '{Ticket}' in reflog entry '{Action}' on {Date} in '{Repo}'",
                        ticket, parsed.Action, day.Date.ToShortDateString(), Path.GetFileName(_defaultRepositoryPath));
                    return ticket;
                }
            }

            Log.Debug("GetTicketFromDayReflogAsync: no ticket found in reflog for {Date} in '{Repo}'",
                day.Date.ToShortDateString(), Path.GetFileName(_defaultRepositoryPath));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GetTicketFromDayReflogAsync: error scanning reflog of '{Repo}' for {Date}",
                Path.GetFileName(_defaultRepositoryPath), day.Date.ToShortDateString());
        }

        return null;
    }
}
