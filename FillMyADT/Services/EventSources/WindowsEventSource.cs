using FillMyADT.Models;
using FillMyADT.Models.Configuration;
using Serilog;
using System.Diagnostics;

namespace FillMyADT.Services.EventSources;

/// <summary>
/// Event source that reads the Windows System event log to create the daily skeleton:
/// work start, lunch break and work end. It builds "awake" intervals from sleep/wake
/// transitions, ignores very short wake-ups (notifications, maintenance) and derives:
/// - work start = start of the first meaningful awake interval
/// - work end   = end of the last meaningful awake interval (none while the machine is still on)
/// - lunch      = the longest idle gap starting between 11:00 and 14:00 (min 30 min logged)
/// Git and Outlook events fill the gaps.
/// </summary>
public class WindowsEventSource : IEventSource
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsEventSource>();

    private const string KernelPower = "Microsoft-Windows-Kernel-Power";
    private const string KernelGeneral = "Microsoft-Windows-Kernel-General";
    private const string PowerTroubleshooter = "Microsoft-Windows-Power-Troubleshooter";
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly TimeSpan DayStart = TimeSpan.FromHours(6);
    // Awake periods shorter than this
    private static readonly TimeSpan MinAwake = TimeSpan.FromMinutes(5);
    // Idle gaps shorter than this are not considered a lunch break
    private static readonly TimeSpan MinLunchIdle = TimeSpan.FromMinutes(10);
    private const int MinBreakMinutes = 30;
    private const int MaxBreakMinutes = 60;

    private readonly WindowsEventSourceConfig _config;

    public string Name => "Windows Events";

    public WindowsEventSource(WindowsEventSourceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    public Task<IEnumerable<Event>> GetEventsAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
    {
        if (!_config.IsEnabled)
            return Task.FromResult<IEnumerable<Event>>([]);

        return Task.Run<IEnumerable<Event>>(() =>
        {
            var events = new List<Event>();

            try
            {
                using var systemLog = new EventLog("System");
                var transitions = systemLog.Entries.Cast<EventLogEntry>()
                    .Where(e => e.TimeGenerated >= startDate && e.TimeGenerated <= endDate)
                    .Select(e => (Entry: e, Kind: Classify(e)))
                    .Where(t => t.Kind != Transition.None)
                    .ToList();

                foreach (var day in transitions.GroupBy(t => t.Entry.TimeGenerated.Date).OrderBy(g => g.Key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Ignore night-time activity (updates, maintenance wake-ups) before 06:00
                    var dayTransitions = day
                        .Where(t => t.Entry.TimeGenerated.TimeOfDay >= DayStart)
                        .OrderBy(t => t.Entry.TimeGenerated)
                        .ToList();
                    events.AddRange(CreateDayEvents(day.Key, dayTransitions));
                }

                Log.Information("Found {EventCount} daily skeleton events (Boot/Lunch/Shutdown)", events.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "Error reading System event log");
            }

            return events.OrderBy(e => e.Timestamp).ToList();
        }, cancellationToken);
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.IsEnabled)
            return Task.FromResult(false);

        try
        {
            return Task.FromResult(EventLog.Exists("System"));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Windows Event Log not available");
            return Task.FromResult(false);
        }
    }

    private enum Transition { None, Sleep, Wake }

    /// <summary>
    /// Sleep: Kernel-Power 42 (sleep), 506 (modern standby entered), 109 (shutdown), Kernel-General 13 (OS shutdown).
    /// Wake:  Kernel-Power 507 (modern standby exited), Power-Troubleshooter 1 (resumed), Kernel-General 12 (OS start).
    /// Deliberately ignored: Kernel-General 1 (system time changed, fires at random times) and
    /// Kernel-Power 107 (resume, but stamped with the suspend time).
    /// </summary>
    private static Transition Classify(EventLogEntry e) => (e.Source, e.InstanceId) switch
    {
        (KernelPower, 42 or 506 or 109) => Transition.Sleep,
        (KernelGeneral, 13) => Transition.Sleep,
        (KernelPower, 507) => Transition.Wake,
        (PowerTroubleshooter, 1) => Transition.Wake,
        (KernelGeneral, 12) => Transition.Wake,
        _ => Transition.None
    };

    private IEnumerable<Event> CreateDayEvents(DateTime date, List<(EventLogEntry Entry, Transition Kind)> transitions)
    {
        // Build awake intervals. If the first transition is a sleep, the machine was already on at 06:00.
        var awake = new List<(DateTime Start, DateTime? End)>();
        DateTime? awakeSince = transitions.Count > 0 && transitions[0].Kind == Transition.Sleep ? date + DayStart : null;
        foreach (var (entry, kind) in transitions)
        {
            if (kind == Transition.Wake && awakeSince == null)
                awakeSince = entry.TimeGenerated;
            else if (kind == Transition.Sleep && awakeSince != null)
            {
                awake.Add((awakeSince.Value, entry.TimeGenerated));
                awakeSince = null;
            }
        }
        if (awakeSince != null)
            awake.Add((awakeSince.Value, null)); // still awake (today) or running past midnight

        var dayEnd = date.AddDays(1);
        var meaningful = awake
            .Where(a => (a.End ?? Min(DateTime.Now, dayEnd)) - a.Start >= MinAwake)
            .ToList();

        if (meaningful.Count == 0)
        {
            Log.Debug("{Date}: no meaningful awake period found in System log", date.ToString("yyyy-MM-dd"));
            yield break;
        }

        var workStart = meaningful[0].Start;
        var workEnd = meaningful[^1].End;

        yield return new Event { Source = Name, Timestamp = workStart, EventType = EventType.Boot, Description = "Machine woke up / started" };
        if (workEnd != null)
            yield return new Event { Source = Name, Timestamp = workEnd.Value, EventType = EventType.Shutdown, Description = "Machine went to sleep / shut down" };

        // Idle gaps between meaningful awake periods; lunch is the longest one starting in the lunch window
        var lunchGap = meaningful
            .Zip(meaningful.Skip(1), (a, b) => (Start: a.End!.Value, End: b.Start))
            .Where(g => g.Start >= date.AddHours(11) && g.Start <= date.AddHours(14) && g.End - g.Start >= MinLunchIdle)
            .OrderByDescending(g => g.End - g.Start)
            .FirstOrDefault();

        Log.Debug("{Date}: {Count} awake periods, work {Start:HH:mm}-{End}, lunch idle {LunchStart}-{LunchEnd}",
            date.ToString("yyyy-MM-dd"), meaningful.Count, workStart, workEnd?.ToString("HH:mm") ?? "(open)",
            lunchGap == default ? "-" : lunchGap.Start.ToString("HH:mm"), lunchGap == default ? "-" : lunchGap.End.ToString("HH:mm"));

        foreach (var e in CreateLunchBreakEvents(date, lunchGap == default ? null : lunchGap))
            yield return e;
    }

    /// <summary>
    /// Lunch is logged from the idle start (rounded to 5 minutes) for the actual idle duration,
    /// but at least 30 and at most 60 minutes. Without a detected idle gap: 12:00-12:30.
    /// </summary>
    private IEnumerable<Event> CreateLunchBreakEvents(DateTime date, (DateTime Start, DateTime End)? idle)
    {
        if (idle == null)
        {
            yield return new Event { Source = Name, Timestamp = date.AddHours(12), EventType = EventType.LunchBreakStart, Description = "Lunch break begins (default time)" };
            yield return new Event { Source = Name, Timestamp = date.AddHours(12.5), EventType = EventType.LunchBreakEnd, Description = "Lunch break ends" };
            yield break;
        }

        var (originalStart, originalEnd) = idle.Value;
        var lunchStart = date + TimeHelper.RoundNearest(TimeOnly.FromDateTime(originalStart), 5).ToTimeSpan();
        var actualMinutes = (int)Math.Ceiling((originalEnd - lunchStart).TotalMinutes / 5.0) * 5;
        var durationMinutes = Math.Clamp(actualMinutes, MinBreakMinutes, MaxBreakMinutes);
        var lunchEnd = lunchStart.AddMinutes(durationMinutes);

        var metadata = new Dictionary<string, string>
        {
            ["OriginalTime"] = originalStart.ToString(DateTimeFormat),
            ["IdleUntil"] = originalEnd.ToString(DateTimeFormat),
            ["DetectionMethod"] = "Standby gap",
            ["Duration"] = $"{durationMinutes} minutes"
        };

        yield return new Event { Source = Name, Timestamp = lunchStart, EventType = EventType.LunchBreakStart, Description = "Lunch break begins (detected from standby)", Metadata = metadata };
        yield return new Event { Source = Name, Timestamp = lunchEnd, EventType = EventType.LunchBreakEnd, Description = "Lunch break ends", Metadata = new Dictionary<string, string>(metadata) };
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
