using FillMyADT.Models;
using FillMyADT.Models.Configuration;
using Serilog;
using System.Diagnostics;

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

    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsEventSource>();

    public string Name => "Windows Events";

    // Track boot and shutdown events using Kernel-General and Kernel-Power events
    private static readonly Dictionary<(long EventId, string Provider), (string EventType, string Category)> PowerEventIds = new()
    {
        // Boot events from Kernel-General
        { (1, "Microsoft-Windows-Kernel-General"), ("Boot", "System boot (cold start)") },
        { (12, "Microsoft-Windows-Kernel-General"), ("Boot", "System resume from hibernation") },

        // Shutdown events from Kernel-Power
        { (107, "Microsoft-Windows-Kernel-Power"), ("Shutdown", "System shutdown initiated") },
        { (42, "Microsoft-Windows-Kernel-Power"), ("Shutdown", "System entering sleep") },
        { (109, "Microsoft-Windows-Kernel-Power"), ("Shutdown", "System entering hibernation") }
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

                // Add lunch break skeleton events for each day that has boot or shutdown events
                var daysWithEvents = systemEvents
                    .Select(e => e.Timestamp.Date)
                    .Distinct()
                    .OrderBy(d => d)
                    .ToList();

                Log.Debug("Processing lunch breaks for {DayCount} unique days", daysWithEvents.Count);

                foreach (var date in daysWithEvents)
                {
                    Log.Debug("Processing lunch break for date: {Date}", date.ToShortDateString());

                    // Find first Kernel-Power event after 11:00 AM (likely lunch break)
                    var lunchTimeCandidate = FindLunchBreakTime(systemLog, date);

                    if (lunchTimeCandidate.HasValue)
                    {
                        var originalTime = lunchTimeCandidate.Value;
                        var lunchStart = RoundToNextQuarterHour(originalTime);
                        var lunchEnd = lunchStart.AddMinutes(30);

                        Log.Debug("Detected lunch break for {Date}: {Start} - {End} (from event at {Original})",
                            date.ToShortDateString(), lunchStart.ToString("HH:mm"), lunchEnd.ToString("HH:mm"), originalTime.ToString("HH:mm"));

                        Log.Information("Adding lunch break events for {Date}: Start={Start}, End={End}",
                            date.ToShortDateString(), lunchStart.ToString("yyyy-MM-dd HH:mm:ss"), lunchEnd.ToString("yyyy-MM-dd HH:mm:ss"));

                        events.Add(new Event
                        {
                            Source = Name,
                            Timestamp = lunchStart,
                            EventType = "Lunch Break Start",
                            Description = "Lunch break begins (detected from system activity)",
                            Metadata = new Dictionary<string, string>
                            {
                                ["OriginalTime"] = originalTime.ToString("yyyy-MM-dd HH:mm:ss"),
                                ["RoundedTime"] = lunchStart.ToString("yyyy-MM-dd HH:mm:ss"),
                                ["DetectionMethod"] = "Kernel-Power event"
                            }
                        });

                        events.Add(new Event
                        {
                            Source = Name,
                            Timestamp = lunchEnd,
                            EventType = "Lunch Break End",
                            Description = "Lunch break ends",
                            Metadata = new Dictionary<string, string>
                            {
                                ["OriginalTime"] = originalTime.ToString("yyyy-MM-dd HH:mm:ss"),
                                ["RoundedTime"] = lunchStart.ToString("yyyy-MM-dd HH:mm:ss"),
                                ["Duration"] = "30 minutes"
                            }
                        });
                    }
                    else
                    {
                        // Fallback to default 12:00-12:30 if no lunch event detected
                        Log.Debug("No lunch break detected for {Date}, using default 12:00-12:30", date.ToShortDateString());

                        Log.Information("Adding default lunch break events for {Date}: Start=12:00, End=12:30", date.ToShortDateString());

                        events.Add(new Event
                        {
                            Source = Name,
                            Timestamp = new DateTime(date.Year, date.Month, date.Day, 12, 0, 0),
                            EventType = "Lunch Break Start",
                            Description = "Lunch break begins (default time)"
                        });

                        events.Add(new Event
                        {
                            Source = Name,
                            Timestamp = new DateTime(date.Year, date.Month, date.Day, 12, 30, 0),
                            EventType = "Lunch Break End",
                            Description = "Lunch break ends"
                        });
                    }
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
            var allEntries = eventLog.Entries.Cast<EventLogEntry>()
                .Where(e => e.TimeGenerated >= startDate && e.TimeGenerated <= endDate)
                .Where(e => IsBootOrShutdownEvent(e))
                .Select(e => new
                {
                    Entry = e,
                    Date = e.TimeGenerated.Date,
                    Info = GetEventInfo(e)
                })
                .ToList();

            Log.Debug("Found {TotalCount} Kernel boot/shutdown events in date range", allEntries.Count);

            // Group by date and get first boot and last shutdown for each day
            var eventsByDay = allEntries.GroupBy(e => e.Date);

            foreach (var dayGroup in eventsByDay)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var date = dayGroup.Key;
                var dayEvents = dayGroup.ToList();

                var bootCount = dayEvents.Count(e => e.Info.EventType == "Boot");
                var shutdownCount = dayEvents.Count(e => e.Info.EventType == "Shutdown");

                Log.Debug("Day {Date}: {BootCount} boot events, {ShutdownCount} shutdown events",
                    date.ToShortDateString(), bootCount, shutdownCount);

                // Get first boot event of the day (morning start)
                var firstBoot = dayEvents
                    .Where(e => e.Info.EventType == "Boot")
                    .OrderBy(e => e.Entry.TimeGenerated)
                    .FirstOrDefault();

                if (firstBoot != null)
                {
                    Log.Debug("Adding morning boot event at {Time}", firstBoot.Entry.TimeGenerated);
                    events.Add(new Event
                    {
                        Source = Name,
                        Timestamp = firstBoot.Entry.TimeGenerated,
                        EventType = firstBoot.Info.EventType,
                        Description = firstBoot.Info.Category,
                        Metadata = new Dictionary<string, string>
                        {
                            ["EventId"] = firstBoot.Entry.InstanceId.ToString(),
                            ["Source"] = firstBoot.Entry.Source
                        }
                    });
                }

                // Get last shutdown event of the day (evening end)
                var lastShutdown = dayEvents
                    .Where(e => e.Info.EventType == "Shutdown")
                    .OrderByDescending(e => e.Entry.TimeGenerated)
                    .FirstOrDefault();

                if (lastShutdown != null)
                {
                    Log.Debug("Adding evening shutdown event at {Time}", lastShutdown.Entry.TimeGenerated);
                    events.Add(new Event
                    {
                        Source = Name,
                        Timestamp = lastShutdown.Entry.TimeGenerated,
                        EventType = lastShutdown.Info.EventType,
                        Description = lastShutdown.Info.Category,
                        Metadata = new Dictionary<string, string>
                        {
                            ["EventId"] = lastShutdown.Entry.InstanceId.ToString(),
                            ["Source"] = lastShutdown.Entry.Source
                        }
                    });
                }
            }

            Log.Debug("ReadBootShutdownEvents returning {Count} events", events.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error reading boot/shutdown events");
        }

        return events;
    }

    private static bool IsBootOrShutdownEvent(EventLogEntry entry)
    {
        // For Kernel events, InstanceId equals EventID
        var key = (entry.InstanceId, entry.Source);
        return PowerEventIds.ContainsKey(key);
    }

    private static (string EventType, string Category) GetEventInfo(EventLogEntry entry)
    {
        // For Kernel events, InstanceId equals EventID
        var key = (entry.InstanceId, entry.Source);
        if (PowerEventIds.TryGetValue(key, out var info))
        {
            return info;
        }

        return ("System Event", "Unknown event");
    }

    /// <summary>
    /// Find the first Kernel-Power event after 11:00 AM on the given date.
    /// Any Kernel-Power event likely indicates when the user took a lunch break.
    /// </summary>
    private static DateTime? FindLunchBreakTime(EventLog eventLog, DateTime date)
    {
        try
        {
            var lunchWindowStart = new DateTime(date.Year, date.Month, date.Day, 11, 0, 0);
            var lunchWindowEnd = new DateTime(date.Year, date.Month, date.Day, 14, 0, 0); // Search until 2 PM

            var lunchEvent = eventLog.Entries.Cast<EventLogEntry>()
                .Where(e => e.TimeGenerated >= lunchWindowStart && e.TimeGenerated <= lunchWindowEnd)
                .Where(e => e.Source == "Microsoft-Windows-Kernel-Power") // Accept ANY Kernel-Power event
                .OrderBy(e => e.TimeGenerated)
                .FirstOrDefault();

            if (lunchEvent != null)
            {
                Log.Debug("Found lunch break candidate: EventID {EventId} at {Time}",
                    lunchEvent.InstanceId, lunchEvent.TimeGenerated.ToString("HH:mm:ss"));
            }

            return lunchEvent?.TimeGenerated;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error finding lunch break time for {Date}", date.ToShortDateString());
            return null;
        }
    }

    /// <summary>
    /// Round a DateTime to the next quarter hour (00, 15, 30, 45).
    /// Example: 11:47 → 12:00, 11:13 → 11:15, 12:00 → 12:00
    /// </summary>
    private static DateTime RoundToNextQuarterHour(DateTime time)
    {
        var minutes = time.Minute;
        var roundedMinutes = (int)Math.Ceiling(minutes / 15.0) * 15;

        if (roundedMinutes == 60)
        {
            return new DateTime(time.Year, time.Month, time.Day, time.Hour, 0, 0).AddHours(1);
        }

        return new DateTime(time.Year, time.Month, time.Day, time.Hour, roundedMinutes, 0);
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
