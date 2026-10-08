using FillMyADT.Models;
using FillMyADT.Services.EventSources;
using Serilog;

namespace FillMyADT.Services;

/// <summary>
/// Holds the event source instances and rebuilds them whenever the configuration changes.
/// </summary>
public class EventSourceRegistry
{
    private static readonly ILogger Log = Serilog.Log.ForContext<EventSourceRegistry>();

    private volatile Snapshot _snapshot;

    public EventSourceRegistry(ConfigurationService configurationService)
    {
        ArgumentNullException.ThrowIfNull(configurationService);
        _snapshot = Build(configurationService.Current);
        configurationService.Changed += config =>
        {
            _snapshot = Build(config);
            Log.Information("Event sources rebuilt after configuration change");
        };
    }

    /// <summary>
    /// All event sources built from the active configuration
    /// </summary>
    public IReadOnlyList<IEventSource> Sources => _snapshot.Sources;

    /// <summary>
    /// The Git source used for branch lookups
    /// </summary>
    public GitEventSource Git => _snapshot.Git;

    private static Snapshot Build(AppConfiguration config)
    {
        var git = new GitEventSource(config.Git);
        IEventSource[] sources =
        [
            new WindowsEventSource(config.Windows),
            git,
            new OutlookEventSource(config.Outlook, config),
            new EdgeEventSource(config.Edge)
        ];
        return new Snapshot(sources, git);
    }

    private sealed record Snapshot(IReadOnlyList<IEventSource> Sources, GitEventSource Git);
}
