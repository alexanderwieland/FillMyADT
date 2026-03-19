using FillMyADT.Models;
using FillMyADT.Services.EventSources;
using Serilog;

namespace FillMyADT.Services;

/// <summary>
/// Service that aggregates events from multiple sources in priority order:
/// 1. Windows Events (Boot/Shutdown/Lunch) - Key structural events
/// 2. Outlook Calendar - Key meeting events
/// 3. Git Events - Fill gaps between key events
/// </summary>
public class EventAggregatorService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<EventAggregatorService>();

    private readonly IEnumerable<IEventSource> _eventSources;

    public EventAggregatorService(IEnumerable<IEventSource> eventSources)
    {
        ArgumentNullException.ThrowIfNull(eventSources);
        _eventSources = eventSources;
        Log.Information("EventAggregatorService initialized with {Count} event sources", eventSources.Count());
    }

    /// <summary>
    /// Get all events from all available sources for the specified date
    /// </summary>
    public async Task<IReadOnlyList<Event>> GetEventsForDayAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var startOfDay = date.Date;
        var endOfDay = startOfDay.AddDays(1).AddTicks(-1);

        return await GetEventsForRangeAsync(startOfDay, endOfDay, cancellationToken);
    }

    /// <summary>
    /// Get all events from all available sources for the specified date range
    /// Sources are executed in priority order: Windows → Outlook → Git
    /// </summary>
    public async Task<IReadOnlyList<Event>> GetEventsForRangeAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
    {
        Log.Information("Aggregating events from {StartDate} to {EndDate}", startDate, endDate);

        // Order sources: Windows first, then Outlook, then Git, then others
        var orderedSources = _eventSources
            .OrderBy(s => s switch
            {
                WindowsEventSource => 0,
                OutlookEventSource => 1,
                GitEventSource => 2,
                _ => 3
            })
            .ToList();

        var allEvents = new List<Event>();

        // Execute sources sequentially in order
        foreach (var source in orderedSources)
        {
            var sourceEvents = await GetEventsFromSourceAsync(source, startDate, endDate, cancellationToken);
            if (sourceEvents != null)
            {
                allEvents.AddRange(sourceEvents);
            }
        }

        var sortedEvents = allEvents.OrderBy(e => e.Timestamp).ToList();

        Log.Information("Aggregated {Count} total events from {SourceCount} sources", sortedEvents.Count, _eventSources.Count());

        return sortedEvents;
    }

    /// <summary>
    /// Get events from a single source (runs in separate thread)
    /// </summary>
    private async Task<IEnumerable<Event>?> GetEventsFromSourceAsync(IEventSource source, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
    {
        try
        {
            if (!await source.IsAvailableAsync(cancellationToken))
            {
                Log.Debug("Event source {SourceName} is not available", source.Name);
                return null;
            }

            Log.Debug("Reading events from {SourceName}", source.Name);
            var events = await source.GetEventsAsync(startDate, endDate, cancellationToken);
            var eventList = events.ToList();

            Log.Information("Retrieved {Count} events from {SourceName}", eventList.Count, source.Name);
            return eventList;
        }
        catch (OperationCanceledException)
        {
            Log.Information("Event retrieval from {SourceName} was cancelled", source.Name);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error retrieving events from {SourceName}", source.Name);
            return null;
        }
    }

    /// <summary>
    /// Get all available event sources
    /// </summary>
    public async Task<IEnumerable<string>> GetAvailableSourcesAsync(CancellationToken cancellationToken = default)
    {
        var availableSources = new List<string>();

        var tasks = _eventSources.Select(async source =>
        {
            try
            {
                if (await source.IsAvailableAsync(cancellationToken))
                {
                    return source.Name;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error checking availability of {SourceName}", source.Name);
            }
            return null;
        });

        var results = await Task.WhenAll(tasks);
        availableSources.AddRange(results.Where(r => r != null)!);

        Log.Debug("Available event sources: {Sources}", string.Join(", ", availableSources));
        return availableSources;
    }
}
