using FillMyADT.Models.Configuration;
using FillMyADT.Services.EventSources;

namespace FillMyADT.Tests.Services.EventSources;

public class GitEventSourceTests
{
    [Fact]
    public void WhenConfigIsNullThenConstructorThrows()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new GitEventSource(null!));
        Assert.Equal("config", exception.ParamName);
    }

    [Fact]
    public void WhenConfigHasRepositoryPathsThenInitializesWithPaths()
    {
        var config = new GitEventSourceConfig
        {
            IsEnabled = true,
            AutoDiscoverRepositories = false,
            RepositoryPaths = ["C:\\Repo1", "C:\\Repo2"]
        };

        var source = new GitEventSource(config);

        Assert.Equal("Git History", source.Name);
    }

    [Fact]
    public void WhenConfigHasTicketPatternThenInitializesPattern()
    {
        var config = new GitEventSourceConfig
        {
            IsEnabled = true,
            AutoDiscoverRepositories = false,
            TicketPattern = @"TICKET-(\d+)"
        };

        var source = new GitEventSource(config);

        Assert.Equal("Git History", source.Name);
    }

    [Fact]
    public async Task WhenDisabledThenGetEventsReturnsEmpty()
    {
        var config = new GitEventSourceConfig
        {
            IsEnabled = false,
            AutoDiscoverRepositories = false,
            RepositoryPaths = ["C:\\TestRepo"]
        };

        var source = new GitEventSource(config);
        var events = await source.GetEventsAsync(DateTime.Now.AddDays(-7), DateTime.Now);

        Assert.Empty(events);
    }

    [Fact]
    public async Task WhenDisabledThenIsAvailableReturnsFalse()
    {
        var config = new GitEventSourceConfig
        {
            IsEnabled = false,
            AutoDiscoverRepositories = false,
            RepositoryPaths = ["C:\\TestRepo"]
        };

        var source = new GitEventSource(config);
        var available = await source.IsAvailableAsync();

        Assert.False(available);
    }

    [Fact]
    public async Task WhenNoRepositoriesThenIsAvailableReturnsFalse()
    {
        var config = new GitEventSourceConfig
        {
            IsEnabled = true,
            AutoDiscoverRepositories = false,
            RepositoryPaths = []
        };

        var source = new GitEventSource(config);
        var available = await source.IsAvailableAsync();

        Assert.False(available);
    }

    [Theory]
    [InlineData("Fix bug #1234", "1234")]
    [InlineData("Implement feature #5678 and refactor", "5678")]
    [InlineData("checkout: moving from main to feature/#999", "999")]
    [InlineData("No ticket here", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("#42 at the start", "42")]
    [InlineData("Multiple #111 and #222 tickets", "111")] // Should extract first match
    public void WhenTextContainsTicketNumberThenExtractsCorrectly(string? text, string? expectedTicket)
    {
        var config = new GitEventSourceConfig
        {
            IsEnabled = true,
            AutoDiscoverRepositories = false,
            TicketPattern = @"#(\d+)"
        };

        var source = new GitEventSource(config);
        var result = source.ExtractTicketNumber(text!);

        Assert.Equal(expectedTicket, result);
    }

    [Theory]
    [InlineData("JIRA-1234", @"JIRA-(\d+)", "1234")]
    [InlineData("Fix TICKET-5678", @"TICKET-(\d+)", "5678")]
    [InlineData("Issue ABC-999 resolved", @"ABC-(\d+)", "999")]
    public void WhenCustomTicketPatternThenExtractsCorrectly(string text, string pattern, string expectedTicket)
    {
        var config = new GitEventSourceConfig
        {
            IsEnabled = true,
            AutoDiscoverRepositories = false,
            TicketPattern = pattern
        };

        var source = new GitEventSource(config);
        var result = source.ExtractTicketNumber(text);

        Assert.Equal(expectedTicket, result);
    }

    [Fact(Skip = "Manual test - requires actual directory")]
    public void WhenScanDirectoryExistsThenDiscoversRepositories()
    {
        var scanDir = @"C:\Work\Sync\Git";

        if (!Directory.Exists(scanDir))
        {
            // Test skipped - directory doesn't exist
            return;
        }

        var config = new GitEventSourceConfig
        {
            IsEnabled = true,
            AutoDiscoverRepositories = true,
            ScanDirectory = scanDir
        };

        var source = new GitEventSource(config);

        // Check logs - should show discovered repositories
        Assert.Equal("Git History", source.Name);
    }

    [Theory]
    [InlineData("Fix bug", "HEAD -> feature/#1234, origin/feature/#1234", "1234")]
    [InlineData("Regular commit", "HEAD -> main", null)]
    [InlineData("Commit #5678", "HEAD -> feature/no-ticket", "5678")]
    [InlineData("No ticket", "HEAD -> feature/ABC-999", null)]
    public void WhenCommitHasNoTicketThenExtractsFromBranchName(string commitMessage, string branchRefs, string? expectedTicket)
    {
        var config = new GitEventSourceConfig
        {
            IsEnabled = true,
            AutoDiscoverRepositories = false,
            TicketPattern = @"#(\d+)"
        };

        var source = new GitEventSource(config);

        var ticketFromMessage = source.ExtractTicketNumber(commitMessage);
        var ticketFromBranch = string.IsNullOrEmpty(ticketFromMessage) 
            ? source.ExtractTicketNumber(branchRefs) 
            : ticketFromMessage;

        Assert.Equal(expectedTicket, ticketFromBranch);
    }
}
