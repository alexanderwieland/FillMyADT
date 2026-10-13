using FillMyADT.Models;
using FillMyADT.Models.Configuration;
using Serilog;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

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
    // Idle gaps (other than lunch) at least this long are reported as shutdown + boot, e.g. commuting home
    private static readonly TimeSpan MinOffPeriod = TimeSpan.FromMinutes(60);
    // Only gaps starting this late count as off periods (left the office); daytime standby (meetings, away from desk) does not
    private static readonly TimeSpan OffPeriodEarliestStart = TimeSpan.FromHours(16);
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
                var transitions = ReadTransitions(startDate, endDate, cancellationToken);
                events.AddRange(CreateEventsFromTransitions(transitions, cancellationToken));

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

    /// <summary>
    /// Power state change read from the System event log
    /// </summary>
    public enum Transition { None, Sleep, Wake }

    /// <summary>
    /// Build the daily skeleton events (Boot/Lunch/Shutdown) from power transitions, grouped per day
    /// </summary>
    public IReadOnlyList<Event> CreateEventsFromTransitions(IEnumerable<(DateTime Time, Transition Kind)> transitions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transitions);

        var events = new List<Event>();
        foreach (var day in transitions.GroupBy(t => t.Time.Date).OrderBy(g => g.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Ignore night-time activity (updates, maintenance wake-ups) before 06:00
            var dayTransitions = day
                .Where(t => t.Time.TimeOfDay >= DayStart)
                .OrderBy(t => t.Time)
                .ToList();
            events.AddRange(CreateDayEvents(day.Key, dayTransitions));
        }
        return events.OrderBy(e => e.Timestamp).ToList();
    }

    /// <summary>
    /// Sleep: Kernel-Power 42 (sleep), 506 (modern standby entered), 109 (shutdown), Kernel-General 13 (OS shutdown).
    /// Wake:  Kernel-Power 507 (modern standby exited), Power-Troubleshooter 1 (resumed), Kernel-General 12 (OS start).
    /// Deliberately ignored: Kernel-General 1 (system time changed, fires at random times) and
    /// Kernel-Power 107 (resume, but stamped with the suspend time).
    /// </summary>
    /// <summary>
    /// Reads only the relevant power events of the requested time range via an XPath query
    /// instead of enumerating the whole System log.
    /// </summary>
    private static List<(DateTime Time, Transition Kind)> ReadTransitions(DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
    {
        var from = startDate.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var to = endDate.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var xpath = $"*[System[Provider[@Name='{KernelPower}' or @Name='{KernelGeneral}' or @Name='{PowerTroubleshooter}']" +
                    $" and TimeCreated[@SystemTime>='{from}' and @SystemTime<='{to}']]]";

        var result = new List<(DateTime Time, Transition Kind)>();
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath));
        for (var record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
        {
            using (record)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record.TimeCreated is not { } time)
                    continue;
                time = time.ToLocalTime();
                if (time < startDate || time > endDate)
                    continue;
                var kind = Classify(record.ProviderName, record.Id);
                if (kind != Transition.None)
                    result.Add((time, kind));
            }
        }
        return result;
    }

    private static Transition Classify(string source, long id) => (source, id) switch
    {
        (KernelPower, 42 or 506 or 109) => Transition.Sleep,
        (KernelGeneral, 13) => Transition.Sleep,
        (KernelPower, 507) => Transition.Wake,
        (PowerTroubleshooter, 1) => Transition.Wake,
        (KernelGeneral, 12) => Transition.Wake,
        _ => Transition.None
    };

    private IEnumerable<Event> CreateDayEvents(DateTime date, List<(DateTime Time, Transition Kind)> transitions)
    {
        // Build awake intervals. If the first transition is a sleep, the machine was already on at 06:00 -
        // unless a wake follows right away (modern standby logs 506 immediately before 507 when resuming).
        var awake = new List<(DateTime Start, DateTime? End)>();
        var alreadyOn = transitions.Count > 0 && transitions[0].Kind == Transition.Sleep
            && !transitions.Skip(1).Any(t => t.Kind == Transition.Wake
                && t.Time - transitions[0].Time <= TimeSpan.FromMinutes(1));
        DateTime? awakeSince = alreadyOn ? date + DayStart : null;
        foreach (var (time, kind) in transitions)
        {
            if (kind == Transition.Wake && awakeSince == null)
                awakeSince = time;
            else if (kind == Transition.Sleep && awakeSince != null)
            {
                awake.Add((awakeSince.Value, time));
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

        var offPeriods = meaningful
            .Zip(meaningful.Skip(1), (a, b) => (Start: a.End!.Value, End: b.Start))
            .Where(g => g != lunchGap && g.Start.TimeOfDay >= OffPeriodEarliestStart && g.End - g.Start >= MinOffPeriod);
        foreach (var (offStart, offEnd) in offPeriods)
        {
            Log.Debug("{Date}: off period {Start:HH:mm}-{End:HH:mm}", date.ToString("yyyy-MM-dd"), offStart, offEnd);
            yield return new Event { Source = Name, Timestamp = offStart, EventType = EventType.Shutdown, Description = "Machine went to sleep / shut down" };
            yield return new Event
            {
                Source = Name, Timestamp = offEnd, EventType = EventType.Boot, Description = "Machine woke up / started again",
                Metadata = new Dictionary<string, string> { ["Resume"] = "true" }
            };
        }
    }

    /// <summary>
    /// Lunch is logged from the idle start (rounded to 15 minutes) for the actual idle duration,
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
        var lunchStart = date + TimeHelper.RoundNearest(TimeOnly.FromDateTime(originalStart), 15).ToTimeSpan();
        var actualMinutes = (int)Math.Ceiling((originalEnd - lunchStart).TotalMinutes / 15.0) * 15;
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
