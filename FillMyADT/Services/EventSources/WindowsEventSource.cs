using System.Diagnostics;
using FillMyADT.Models;
using FillMyADT.Models.Configuration;
using Serilog;

namespace FillMyADT.Services.EventSources;

/// <summary>
/// Event source that reads Windows Event Log to create daily skeleton structure:
/// 1. Morning boot time
/// 2. Lunch break start (12:00 PM fixed)
/// 3. Lunch break end (12:30 PM fixed)
/// 4. Evening shutdown time
/// These skeleton events define the structure of the day. Git and Outlook events fill the gaps.
/// </summary>
public class WindowsEventSource : IEventSource
{
    private readonly WindowsEventSourceConfig _config;

    public string Name => "Windows Events";

    // Only track boot and shutdown - lunch break is fixed skeleton
    private static readonly Dictionary<(long EventId, string Provider), (string EventType, string Category)> PowerEventIds = new()
    {
        // Boot - morning startup
        { (6005, "EventLog"), ("Boot", "System started") },

        // Shutdown - evening shutdown
        { (6006, "EventLog"), ("Shutdown", "System shutdown") },
        { (6008, "EventLog"), ("Shutdown", "Unexpected shutdown") }
    };

    public WindowsEventSource(WindowsEventSourceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        Log.Information("WindowsEventSource initialized - creating daily skeleton (Boot/Lunch/Shutdown)");
    }

    public async Task<IEnumerable<Event>> GetEventsAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
    {
        if (!_config.IsEnabled)
        {
            Log.Debug("WindowsEventSource is disabled");
            return [];
        }

        return await Task.Run(() =>
        {
            var events = new List<Event>();

            try
            {
                using var systemLog = new EventLog("System");

                // Get boot and shutdown events
                var systemEvents = ReadBootShutdownEvents(systemLog, startDate, endDate, cancellationToken).ToList();
                events.AddRange(systemEvents);

                // Add lunch break skeleton events if we have a boot event
                var bootEvent = events.FirstOrDefault(e => e.EventType == "Boot");
                if (bootEvent != null)
                {
                    var date = bootEvent.Timestamp.Date;
                    
                    // Add lunch break start event (12:00 PM by default)
                    events.Add(new Event
                    {
                        Source = Name,
                        Timestamp = new DateTime(date.Year, date.Month, date.Day, 12, 0, 0),
                        EventType = "Lunch Break Start",
                        Description = "Lunch break begins"
                    });

                    // Add lunch break end event (12:30 PM by default)
                    events.Add(new Event
                    {
                        Source = Name,
                        Timestamp = new DateTime(date.Year, date.Month, date.Day, 12, 30, 0),
                        EventType = "Lunch Break End",
                        Description = "Lunch break ends"
                    });
                }

                Log.Information("Found {EventCount} daily skeleton events (Boot/Lunch Start/Lunch End/Shutdown)", events.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error reading System event log");
            }

            return events.OrderBy(e => e.Timestamp).ToList();
        }, cancellationToken);
    }

    private IEnumerable<Event> ReadBootShutdownEvents(EventLog eventLog, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
    {
        var events = new List<Event>();

        try
        {
            var entries = eventLog.Entries.Cast<EventLogEntry>()
                .Where(e => e.TimeGenerated >= startDate && e.TimeGenerated <= endDate)
                .Where(e => IsBootOrShutdownEvent(e))
                .OrderBy(e => e.TimeGenerated)
                .ToList();

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (eventType, category) = GetEventInfo(entry);

                events.Add(new Event
                {
                    Source = Name,
                    Timestamp = entry.TimeGenerated,
                    EventType = eventType,
                    Description = category,
                    Metadata = new Dictionary<string, string>
                    {
                        ["EventId"] = entry.InstanceId.ToString(),
                        ["Source"] = entry.Source
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error reading boot/shutdown events");
        }

        return events;
    }

    private static bool IsBootOrShutdownEvent(EventLogEntry entry)
    {
        var key = (entry.InstanceId, entry.Source);
        return PowerEventIds.ContainsKey(key);
    }

    private static (string EventType, string Category) GetEventInfo(EventLogEntry entry)
    {
        var key = (entry.InstanceId, entry.Source);
        if (PowerEventIds.TryGetValue(key, out var info))
        {
            return info;
        }

        return ("System Event", "Unknown event");
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.IsEnabled)
            return Task.FromResult(false);

        try
        {
            using var systemLog = new EventLog("System");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Windows Event Log not available");
            return Task.FromResult(false);
        }
    }
}
