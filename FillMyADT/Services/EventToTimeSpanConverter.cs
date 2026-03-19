using FillMyADT.Models;
using FillMyADT.Services.EventSources;
using Serilog;

namespace FillMyADT.Services;

/// <summary>
/// Converts events into time slots with smart rounding and gap detection.
/// Treats Windows and Outlook events as "key events" that define structure,
/// and uses Git events to fill the gaps with work activity.
/// </summary>
public class EventToTimeSpanConverter
{
    private readonly GitEventSource? _gitEventSource;

    private const int StandardRoundingMinutes = 15;
    private const int FirstSlotRoundingMinutes = 5;
    private const int LastSlotRoundingMinutes = 5;

    // Lunch break rules
    private static readonly TimeOnly BreakWindowStart = new(11, 15);
    private static readonly TimeOnly BreakWindowEnd = new(14, 0);
    private const int MinBreakMinutes = 30;
    private const int MaxBreakMinutes = 60;
    private const int PreferredBreakMinutes = 45;
    private static readonly TimeOnly DefaultBreakStart = new(12, 0);
    private static readonly TimeOnly DefaultBreakEnd = new(12, 30);

    public EventToTimeSpanConverter(GitEventSource? gitEventSource = null)
    {
        _gitEventSource = gitEventSource;
    }

    /// <summary>
    /// Convert events to time slots with proper rounding and key event detection.
    /// Windows and Outlook events are treated as "key events" that structure the day.
    /// Git events fill the gaps between key events.
    /// For gaps without Git events, uses the branch that was active at that time.
    /// </summary>
    public async Task<IReadOnlyList<TimeSlot>> ConvertEventsToTimeSlotsAsync(IReadOnlyList<Event> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return [];
        }

        // Build branch history for the day from Git reflog (branch switches)
        var branchHistory = await BuildBranchHistoryAsync(events, cancellationToken);

        return ConvertEventsToTimeSlotsInternal(events, branchHistory);
    }

    /// <summary>
    /// Build a timeline of which branch was active at different times during the day
    /// </summary>
    private async Task<List<(DateTime Time, string Branch, string? Ticket)>> BuildBranchHistoryAsync(IReadOnlyList<Event> events, CancellationToken cancellationToken)
    {
        var branchHistory = new List<(DateTime Time, string Branch, string? Ticket)>();

        if (_gitEventSource == null)
            return branchHistory;

        try
        {
            // Find all branch switch events from Git
            var branchSwitches = events
                .Where(e => e.EventType == "Branch Switch")
                .OrderBy(e => e.Timestamp)
                .ToList();

            foreach (var switchEvent in branchSwitches)
            {
                if (switchEvent.Metadata != null && switchEvent.Metadata.TryGetValue("Branch", out var branchName))
                {
                    var ticket = switchEvent.Metadata.TryGetValue("TicketNumber", out var t) ? t : _gitEventSource.ExtractTicketNumber(branchName);
                    branchHistory.Add((switchEvent.Timestamp, branchName, ticket));
                    Log.Debug("Branch switch at {Time}: {Branch} (Ticket: {Ticket})", 
                        switchEvent.Timestamp, branchName, ticket ?? "none");
                }
            }

            // If no branch switches found, get current branch as fallback
            if (branchHistory.Count == 0)
            {
                var currentBranch = await _gitEventSource.GetCurrentBranchAsync(cancellationToken);
                if (!string.IsNullOrEmpty(currentBranch))
                {
                    var ticket = _gitEventSource.ExtractTicketNumber(currentBranch);
                    branchHistory.Add((DateTime.MinValue, currentBranch, ticket));
                    Log.Information("No branch switches found, using current branch: {Branch} (Ticket: {Ticket})", 
                        currentBranch, ticket ?? "none");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not build branch history");
        }

        return branchHistory;
    }

    /// <summary>
    /// Get the ticket number for the branch that was active at a specific time
    /// </summary>
    private string? GetTicketForTimeFromBranchHistory(DateTime time, List<(DateTime Time, string Branch, string? Ticket)> branchHistory)
    {
        if (branchHistory.Count == 0)
            return null;

        // Find the most recent branch switch before or at this time
        var activeBranch = branchHistory
            .Where(b => b.Time <= time)
            .OrderByDescending(b => b.Time)
            .FirstOrDefault();

        // If found, return its ticket, otherwise use the earliest branch (if available)
        if (activeBranch != default)
        {
            return activeBranch.Ticket;
        }

        // Fallback to first branch if time is before all switches
        return branchHistory.FirstOrDefault().Ticket;
    }

    /// <summary>
    /// Convert events to time slots (synchronous version for backward compatibility)
    /// </summary>
    public IReadOnlyList<TimeSlot> ConvertEventsToTimeSlots(IReadOnlyList<Event> events)
    {
        return ConvertEventsToTimeSlotsInternal(events, branchHistory: []);
    }

    /// <summary>
    /// Internal implementation of time slot conversion
    /// </summary>
    private IReadOnlyList<TimeSlot> ConvertEventsToTimeSlotsInternal(IReadOnlyList<Event> events, List<(DateTime Time, string Branch, string? Ticket)> branchHistory)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return [];
        }

        var sortedEvents = events.OrderBy(e => e.Timestamp).ToList();

        // Check for special whole-day events (Homeoffice or Holiday)
        var specialEvent = sortedEvents.FirstOrDefault(e => 
            e.EventType == "SpecialWholeDay-Homeoffice" || 
            e.EventType == "SpecialWholeDay-Holiday");

        if (specialEvent != null)
        {
            // Return a single special timeslot for the whole day
            return CreateSpecialWholeDayTimeSlot(specialEvent);
        }

        // Find Boot and Shutdown events to determine work boundaries
        var bootEvent = sortedEvents.FirstOrDefault(e => e.EventType == "Boot");
        var shutdownEvent = sortedEvents.LastOrDefault(e => e.EventType == "Shutdown");

        if (bootEvent == null)
        {
            Log.Warning("No Boot event found, cannot create time slots");
            return [];
        }

        var date = DateOnly.FromDateTime(bootEvent.Timestamp.Date);

        // First slot: boot rounded up to 5min, until next quarter hour
        var firstSlotStart = RoundUpToNearest(TimeOnly.FromDateTime(bootEvent.Timestamp), FirstSlotRoundingMinutes);
        var firstSlotEnd = RoundUpToNextQuarterHour(firstSlotStart);

        // If first slot would be 0 duration, extend to next quarter (15 min minimum)
        if (firstSlotStart == firstSlotEnd)
        {
            firstSlotEnd = firstSlotEnd.AddMinutes(15);
        }

        // Work slots start after first slot
        var workStart = firstSlotEnd;
        var workEnd = shutdownEvent != null 
            ? RoundDownToNearest(TimeOnly.FromDateTime(shutdownEvent.Timestamp), LastSlotRoundingMinutes)
            : new TimeOnly(17, 0); // Default to 5 PM if no shutdown

        // Extract calendar meetings from events
        var calendarMeetings = ExtractCalendarMeetings(sortedEvents, workStart, workEnd);

        // Get all work-related events (commits) between work start and end
        var workEvents = sortedEvents
            .Where(e => e.EventType != "Boot" && e.EventType != "Shutdown" 
                     && e.EventType != "CalendarMeetingStart" && e.EventType != "CalendarMeetingEnd"
                     && e.EventType != "ReviewStart" && e.EventType != "ReviewEnd")
            .Where(e => TimeOnly.FromDateTime(e.Timestamp) >= workStart && 
                       TimeOnly.FromDateTime(e.Timestamp) <= workEnd)
            .ToList();

        var timeSlots = new List<TimeSlot>();

        // Add the first slot (startup slot)
        timeSlots.Add(new TimeSlot
        {
            Date = date,
            StartTime = firstSlotStart,
            EndTime = firstSlotEnd,
            TicketNr = null,
            Text = "Startup",
            Source = GetSourceFromCategory(TimeSlotCategory.Startup),
            Category = TimeSlotCategory.Startup
        });

        // Create work and meeting slots
        if (calendarMeetings.Count == 0)
        {
            // No meetings - use original logic
            CreateWorkSlotsWithNoonBreak(timeSlots, date, workStart, workEnd, workEvents, branchHistory);
        }
        else
        {
            // Have meetings - create granular slots
            CreateGranularSlotsWithMeetings(timeSlots, date, workStart, workEnd, workEvents, calendarMeetings, branchHistory);
        }

        Log.Information("Converted {EventCount} events into {SlotCount} time slots", events.Count, timeSlots.Count);
        return timeSlots;
    }

    private record CalendarMeeting(TimeOnly Start, TimeOnly End, string Subject, string? Location, string? TicketNumber = null);

    /// <summary>
    /// Extract and consolidate overlapping calendar meetings including lunch break
    /// </summary>
    private List<CalendarMeeting> ExtractCalendarMeetings(List<Event> events, TimeOnly workStart, TimeOnly workEnd)
    {
        var meetingStarts = events
            .Where(e => e.EventType == "CalendarMeetingStart" || e.EventType == "ReviewStart")
            .Where(e => TimeOnly.FromDateTime(e.Timestamp) < workEnd)
            .ToList();

        var meetings = new List<CalendarMeeting>();

        // Add lunch break as a meeting if present
        var lunchStart = events.FirstOrDefault(e => e.EventType == "Lunch Break Start");
        var lunchEnd = events.FirstOrDefault(e => e.EventType == "Lunch Break End");

        if (lunchStart != null && lunchEnd != null)
        {
            var lunchStartTime = TimeOnly.FromDateTime(lunchStart.Timestamp);
            var lunchEndTime = TimeOnly.FromDateTime(lunchEnd.Timestamp);

            // Only add lunch if it's within work hours
            if (lunchStartTime >= workStart && lunchEndTime <= workEnd)
            {
                meetings.Add(new CalendarMeeting(lunchStartTime, lunchEndTime, "Lunch Break", null));
                Log.Debug("Added lunch break to schedule: {Start} - {End}", lunchStartTime, lunchEndTime);
            }
        }

        foreach (var startEvent in meetingStarts)
        {
            // Match corresponding end event (CalendarMeetingEnd or ReviewEnd)
            var expectedEndType = startEvent.EventType == "ReviewStart" ? "ReviewEnd" : "CalendarMeetingEnd";

            var endEvent = events.FirstOrDefault(e => 
                e.EventType == expectedEndType && 
                e.Description == startEvent.Description &&
                e.Timestamp >= startEvent.Timestamp);

            if (endEvent == null)
                continue;

            var start = TimeOnly.FromDateTime(startEvent.Timestamp);
            var end = TimeOnly.FromDateTime(endEvent.Timestamp);

            // Clip to work hours
            if (end <= workStart || start >= workEnd)
                continue;

            if (start < workStart)
                start = workStart;
            if (end > workEnd)
                end = workEnd;

            var location = startEvent.Metadata?.GetValueOrDefault("Location");
            var ticketNumber = startEvent.Metadata?.GetValueOrDefault("TicketNumber");
            meetings.Add(new CalendarMeeting(start, end, startEvent.Description ?? "Meeting", location, ticketNumber));
        }

        // Handle overlaps by adjusting times
        meetings = ResolveOverlappingMeetings(meetings);

        return meetings.OrderBy(m => m.Start).ToList();
    }

    /// <summary>
    /// Resolve overlapping meetings by adjusting end/start times
    /// </summary>
    private List<CalendarMeeting> ResolveOverlappingMeetings(List<CalendarMeeting> meetings)
    {
        if (meetings.Count <= 1)
            return meetings;

        var sorted = meetings.OrderBy(m => m.Start).ToList();
        var resolved = new List<CalendarMeeting>();

        for (int i = 0; i < sorted.Count; i++)
        {
            var current = sorted[i];
            var start = current.Start;
            var end = current.End;

            // Check if next meeting overlaps
            if (i < sorted.Count - 1)
            {
                var next = sorted[i + 1];
                if (end > next.Start)
                {
                    // Overlap detected - adjust current meeting end to next meeting start
                    end = next.Start;
                    Log.Debug("Adjusted overlapping meeting: {Subject} end time to {End}", current.Subject, end);
                }
            }

            // Only add if duration is still positive
            if (end > start)
            {
                resolved.Add(new CalendarMeeting(start, end, current.Subject, current.Location, current.TicketNumber));
            }
        }

        return resolved;
    }

    /// <summary>
    /// Create work slots with lunch break detection
    /// </summary>
    private void CreateWorkSlotsWithNoonBreak(List<TimeSlot> timeSlots, DateOnly date, TimeOnly workStart, TimeOnly workEnd, List<Event> workEvents, List<(DateTime Time, string Branch, string? Ticket)> branchHistory)
    {
        // Check if work day spans the lunch window
        if (workStart >= BreakWindowEnd || workEnd <= BreakWindowStart)
        {
            // Work doesn't span lunch window - single slot
            var slotTime = date.ToDateTime(workStart);
            timeSlots.Add(new TimeSlot
            {
                Date = date,
                StartTime = workStart,
                EndTime = workEnd,
                TicketNr = GetTicketNumberFromEvents(workEvents, slotTime, date, branchHistory),
                Text = GetWorkDescription(workEvents),
                Source = GetSourceFromCategory(TimeSlotCategory.Work),
                Category = TimeSlotCategory.Work
            });
            return;
        }

        // Work spans lunch window - need break
        var breakStart = DefaultBreakStart;
        var breakEnd = DefaultBreakEnd;

        // Find appropriate break from work events gap
        var lunchGap = FindLunchBreakGap(workEvents);
        if (lunchGap.HasValue)
        {
            breakStart = lunchGap.Value.Start;
            breakEnd = lunchGap.Value.End;
            Log.Information("Found lunch break gap: {Start} - {End}", breakStart, breakEnd);
        }
        else
        {
            Log.Information("No appropriate lunch gap found, using default: {Start} - {End}", breakStart, breakEnd);
        }

        // Adjust break to not exceed work boundaries
        if (breakStart < workStart)
            breakStart = workStart;
        if (breakEnd > workEnd)
            breakEnd = workEnd;

        // Split into morning and afternoon
        var morningEvents = workEvents.Where(e => TimeOnly.FromDateTime(e.Timestamp) < breakStart).ToList();
        var afternoonEvents = workEvents.Where(e => TimeOnly.FromDateTime(e.Timestamp) >= breakEnd).ToList();

        // Morning slot
        if (workStart < breakStart)
        {
            var morningSlotTime = date.ToDateTime(workStart);
            timeSlots.Add(new TimeSlot
            {
                Date = date,
                StartTime = workStart,
                EndTime = breakStart,
                TicketNr = GetTicketNumberFromEvents(morningEvents.Any() ? morningEvents : workEvents, morningSlotTime, date, branchHistory),
                Text = GetWorkDescription(morningEvents.Any() ? morningEvents : workEvents),
                Source = GetSourceFromCategory(TimeSlotCategory.Work),
                Category = TimeSlotCategory.Work
            });
        }

        // Afternoon slot
        if (breakEnd < workEnd)
        {
            var afternoonSlotTime = date.ToDateTime(breakEnd);
            timeSlots.Add(new TimeSlot
            {
                Date = date,
                StartTime = breakEnd,
                EndTime = workEnd,
                TicketNr = GetTicketNumberFromEvents(afternoonEvents.Any() ? afternoonEvents : (morningEvents.Any() ? morningEvents : workEvents), afternoonSlotTime, date, branchHistory),
                Text = afternoonEvents.Any() ? GetWorkDescription(afternoonEvents) : GetWorkDescription(morningEvents.Any() ? morningEvents : workEvents),
                Source = GetSourceFromCategory(TimeSlotCategory.Work),
                Category = TimeSlotCategory.Work
            });
        }
    }

    /// <summary>
    /// Find an appropriate lunch break gap in work events
    /// </summary>
    private (TimeOnly Start, TimeOnly End)? FindLunchBreakGap(List<Event> workEvents)
    {
        if (workEvents.Count == 0)
            return null;

        var eventsInWindow = workEvents
            .Where(e => TimeOnly.FromDateTime(e.Timestamp) >= BreakWindowStart && 
                       TimeOnly.FromDateTime(e.Timestamp) <= BreakWindowEnd)
            .OrderBy(e => e.Timestamp)
            .ToList();

        if (eventsInWindow.Count < 2)
            return null;

        // Look for gaps between events
        for (int i = 0; i < eventsInWindow.Count - 1; i++)
        {
            var gapStart = TimeOnly.FromDateTime(eventsInWindow[i].Timestamp);
            var gapEnd = TimeOnly.FromDateTime(eventsInWindow[i + 1].Timestamp);
            var gapDuration = (gapEnd - gapStart).TotalMinutes;

            // Check if gap is appropriate for lunch break
            if (gapDuration >= MinBreakMinutes && gapDuration <= MaxBreakMinutes)
            {
                return (gapStart, gapEnd);
            }
        }

        return null;
    }

    /// <summary>
    /// Create granular slots with meetings and work periods in between
    /// </summary>
    private void CreateGranularSlotsWithMeetings(List<TimeSlot> timeSlots, DateOnly date, TimeOnly workStart, TimeOnly workEnd, List<Event> workEvents, List<CalendarMeeting> meetings, List<(DateTime Time, string Branch, string? Ticket)> branchHistory)
    {
        // Check if lunch break is already in meetings (from WindowsEventSource)
        var hasLunchBreakMeeting = meetings.Any(m => m.Subject == "Lunch Break");

        // Detect lunch break from meetings ONLY if not already present
        var lunchBreak = hasLunchBreakMeeting ? null : DetectLunchBreakFromMeetings(meetings);

        if (hasLunchBreakMeeting)
        {
            Log.Debug("Lunch break already in meetings list, skipping detection");
        }

        var currentTime = workStart;
        var lunchBreakInserted = false;

        foreach (var meeting in meetings)
        {
            // Check if we should insert lunch break before this meeting
            if (!lunchBreakInserted && lunchBreak.HasValue && currentTime < lunchBreak.Value.Start && meeting.Start >= lunchBreak.Value.End)
            {
                // Add work before lunch
                if (currentTime < lunchBreak.Value.Start)
                {
                    var eventsBeforeLunch = workEvents
                        .Where(e => TimeOnly.FromDateTime(e.Timestamp) >= currentTime && 
                                   TimeOnly.FromDateTime(e.Timestamp) < lunchBreak.Value.Start)
                        .ToList();

                    var slotTime = date.ToDateTime(currentTime);
                    timeSlots.Add(new TimeSlot
                    {
                        Date = date,
                        StartTime = currentTime,
                        EndTime = lunchBreak.Value.Start,
                        TicketNr = GetTicketNumberFromEvents(eventsBeforeLunch.Any() ? eventsBeforeLunch : workEvents, slotTime, date, branchHistory),
                        Text = GetWorkDescription(eventsBeforeLunch.Any() ? eventsBeforeLunch : workEvents),
                        Location = null,
                        Category = TimeSlotCategory.Work
                    });
                }

                currentTime = lunchBreak.Value.End;
                lunchBreakInserted = true;
            }

            // Add work slot before meeting if there's a gap
            if (currentTime < meeting.Start)
            {
                var eventsInPeriod = workEvents
                    .Where(e => TimeOnly.FromDateTime(e.Timestamp) >= currentTime && 
                               TimeOnly.FromDateTime(e.Timestamp) < meeting.Start)
                    .ToList();

                var slotTime = date.ToDateTime(currentTime);
                timeSlots.Add(new TimeSlot
                {
                    Date = date,
                    StartTime = currentTime,
                    EndTime = meeting.Start,
                    TicketNr = GetTicketNumberFromEvents(eventsInPeriod.Any() ? eventsInPeriod : workEvents, slotTime, date, branchHistory),
                    Text = GetWorkDescription(eventsInPeriod.Any() ? eventsInPeriod : workEvents),
                    Location = null,
                    Source = GetSourceFromCategory(TimeSlotCategory.Work),
                    Category = TimeSlotCategory.Work
                });
            }

            // Add meeting slot with appropriate category
            var category = DetermineMeetingCategory(meeting);
            timeSlots.Add(new TimeSlot
            {
                Date = date,
                StartTime = meeting.Start,
                EndTime = meeting.End,
                TicketNr = meeting.TicketNumber,
                Text = meeting.Subject,
                Location = meeting.Location,
                Source = GetSourceFromCategory(category),
                Category = category
            });

            currentTime = meeting.End;
        }

        // If lunch break wasn't inserted yet and we have time for it
        if (!lunchBreakInserted && lunchBreak.HasValue && currentTime < lunchBreak.Value.Start && lunchBreak.Value.End <= workEnd)
        {
            // Add work before lunch
            if (currentTime < lunchBreak.Value.Start)
            {
                var eventsBeforeLunch = workEvents
                    .Where(e => TimeOnly.FromDateTime(e.Timestamp) >= currentTime && 
                               TimeOnly.FromDateTime(e.Timestamp) < lunchBreak.Value.Start)
                    .ToList();

                var slotTime = date.ToDateTime(currentTime);
                timeSlots.Add(new TimeSlot
                {
                    Date = date,
                    StartTime = currentTime,
                    EndTime = lunchBreak.Value.Start,
                    TicketNr = GetTicketNumberFromEvents(eventsBeforeLunch.Any() ? eventsBeforeLunch : workEvents, slotTime, date, branchHistory),
                    Text = GetWorkDescription(eventsBeforeLunch.Any() ? eventsBeforeLunch : workEvents),
                    Location = null,
                    Source = GetSourceFromCategory(TimeSlotCategory.Work),
                    Category = TimeSlotCategory.Work
                });
            }

            currentTime = lunchBreak.Value.End;
        }

        // Add final work slot if there's time remaining
        if (currentTime < workEnd)
        {
            var eventsInPeriod = workEvents
                .Where(e => TimeOnly.FromDateTime(e.Timestamp) >= currentTime && 
                           TimeOnly.FromDateTime(e.Timestamp) <= workEnd)
                .ToList();

            var slotTime = date.ToDateTime(currentTime);
            timeSlots.Add(new TimeSlot
            {
                Date = date,
                StartTime = currentTime,
                EndTime = workEnd,
                TicketNr = GetTicketNumberFromEvents(eventsInPeriod.Any() ? eventsInPeriod : workEvents, slotTime, date, branchHistory),
                Text = GetWorkDescription(eventsInPeriod.Any() ? eventsInPeriod : workEvents),
                Location = null,
                Source = GetSourceFromCategory(TimeSlotCategory.Work),
                Category = TimeSlotCategory.Work
            });
        }
    }

    /// <summary>
    /// Detect lunch break from calendar meetings or use default
    /// </summary>
    private (TimeOnly Start, TimeOnly End)? DetectLunchBreakFromMeetings(List<CalendarMeeting> meetings)
    {
        if (meetings.Count == 0)
            return (DefaultBreakStart, DefaultBreakEnd);

        // Look for gaps between meetings in the lunch window
        var meetingsInWindow = meetings
            .Where(m => m.End > BreakWindowStart && m.Start < BreakWindowEnd)
            .OrderBy(m => m.Start)
            .ToList();

        if (meetingsInWindow.Count == 0)
        {
            // No meetings in lunch window - use default
            return (DefaultBreakStart, DefaultBreakEnd);
        }

        // Check gaps between meetings
        for (int i = 0; i < meetingsInWindow.Count - 1; i++)
        {
            var gapStart = meetingsInWindow[i].End;
            var gapEnd = meetingsInWindow[i + 1].Start;

            // Gap must be within lunch window
            if (gapStart < BreakWindowStart)
                gapStart = BreakWindowStart;
            if (gapEnd > BreakWindowEnd)
                gapEnd = BreakWindowEnd;

            var gapDuration = (gapEnd - gapStart).TotalMinutes;

            // Check if gap meets break criteria (30-60 min)
            if (gapDuration >= MinBreakMinutes && gapDuration <= MaxBreakMinutes)
            {
                Log.Information("Detected lunch break from meeting gap: {Start} - {End} ({Duration} min)", 
                    gapStart, gapEnd, gapDuration);
                return (gapStart, gapEnd);
            }
        }

        // Check if there's time before first meeting
        if (meetingsInWindow[0].Start > BreakWindowStart)
        {
            var gapStart = BreakWindowStart;
            var gapEnd = meetingsInWindow[0].Start;
            var gapDuration = (gapEnd - gapStart).TotalMinutes;

            if (gapDuration >= MinBreakMinutes && gapDuration <= MaxBreakMinutes)
            {
                Log.Information("Detected lunch break before meetings: {Start} - {End} ({Duration} min)", 
                    gapStart, gapEnd, gapDuration);
                return (gapStart, gapEnd);
            }
        }

        // Check if there's time after last meeting
        var lastMeeting = meetingsInWindow[^1];
        if (lastMeeting.End < BreakWindowEnd)
        {
            var gapStart = lastMeeting.End;
            var gapEnd = BreakWindowEnd;
            var gapDuration = (gapEnd - gapStart).TotalMinutes;

            if (gapDuration >= MinBreakMinutes && gapDuration <= MaxBreakMinutes)
            {
                Log.Information("Detected lunch break after meetings: {Start} - {End} ({Duration} min)", 
                    gapStart, gapEnd, gapDuration);
                return (gapStart, gapEnd);
            }
        }

        // No appropriate gap found - use default
        return (DefaultBreakStart, DefaultBreakEnd);
    }

    /// <summary>
    /// Round time UP to nearest interval
    /// </summary>
    private TimeOnly RoundUpToNearest(TimeOnly time, int minutes)
    {
        var totalMinutes = time.Hour * 60 + time.Minute;
        var roundedMinutes = (int)Math.Ceiling(totalMinutes / (double)minutes) * minutes;

        if (roundedMinutes >= 24 * 60)
        {
            roundedMinutes = 24 * 60 - minutes;
        }

        return new TimeOnly(roundedMinutes / 60, roundedMinutes % 60);
    }

    /// <summary>
    /// Round time UP to next quarter hour (00, 15, 30, 45)
    /// </summary>
    private TimeOnly RoundUpToNextQuarterHour(TimeOnly time)
    {
        var totalMinutes = time.Hour * 60 + time.Minute;
        var roundedMinutes = (int)Math.Ceiling(totalMinutes / 15.0) * 15;

        if (roundedMinutes >= 24 * 60)
        {
            roundedMinutes = 24 * 60 - 15;
        }

        return new TimeOnly(roundedMinutes / 60, roundedMinutes % 60);
    }

    /// <summary>
    /// Round time DOWN to nearest interval
    /// </summary>
    private TimeOnly RoundDownToNearest(TimeOnly time, int minutes)
    {
        var totalMinutes = time.Hour * 60 + time.Minute;
        var roundedMinutes = (int)Math.Floor(totalMinutes / (double)minutes) * minutes;
        return new TimeOnly(roundedMinutes / 60, roundedMinutes % 60);
    }

    /// <summary>
    /// Round time to nearest interval
    /// </summary>
    private TimeOnly RoundToNearest(TimeOnly time, int minutes)
    {
        var totalMinutes = time.Hour * 60 + time.Minute;
        var roundedMinutes = (int)Math.Round(totalMinutes / (double)minutes) * minutes;

        if (roundedMinutes >= 24 * 60)
        {
            roundedMinutes = 24 * 60 - minutes;
        }

        return new TimeOnly(roundedMinutes / 60, roundedMinutes % 60);
    }

    /// <summary>
    /// Extract ticket number from event metadata, with fallback to current branch ticket
    /// </summary>
    private string? GetTicketNumber(Event? evt, string? fallbackTicketNumber = null)
    {
        if (evt?.Metadata != null && evt.Metadata.TryGetValue("TicketNumber", out var ticket))
        {
            return ticket;
        }
        return fallbackTicketNumber;
    }

    /// <summary>
    /// Determine the category of a meeting based on its location metadata
    /// </summary>
    private static TimeSlotCategory DetermineMeetingCategory(CalendarMeeting meeting)
    {
        // Check for lunch break
        if (meeting.Subject == "Lunch Break")
            return TimeSlotCategory.Break;

        // Check location metadata to distinguish Edge events from Outlook
        if (meeting.Location == "Redmine")
            return TimeSlotCategory.RedmineTickets;

        if (meeting.Location == "TFS")
            return TimeSlotCategory.TfsWork;

        // Default to Outlook meeting
        return TimeSlotCategory.Meeting;
    }

    /// <summary>
    /// Get source name from category
    /// </summary>
    private static string GetSourceFromCategory(TimeSlotCategory category) => category switch
    {
        TimeSlotCategory.Startup => "Windows",
        TimeSlotCategory.Work => "Git",
        TimeSlotCategory.Meeting => "Outlook",
        TimeSlotCategory.Break => "System",
        TimeSlotCategory.RedmineTickets => "Edge Browser",
        TimeSlotCategory.TfsWork => "Edge Browser",
        TimeSlotCategory.Homeoffice => "Outlook",
        TimeSlotCategory.Holiday => "Outlook",
        TimeSlotCategory.Other => "Unknown",
        _ => "Unknown"
    };

    /// <summary>
    /// Create a single special whole-day timeslot for Homeoffice or Holiday
    /// </summary>
    private IReadOnlyList<TimeSlot> CreateSpecialWholeDayTimeSlot(Event specialEvent)
    {
        var date = DateOnly.FromDateTime(specialEvent.Timestamp);
        var category = specialEvent.EventType == "SpecialWholeDay-Homeoffice" 
            ? TimeSlotCategory.Homeoffice 
            : TimeSlotCategory.Holiday;

        var description = specialEvent.Description ?? (category == TimeSlotCategory.Homeoffice 
            ? "Home Office" 
            : "Holiday");

        var timeSlot = new TimeSlot
        {
            Date = date,
            StartTime = new TimeOnly(8, 0),  // Start at 8:00 AM
            EndTime = new TimeOnly(17, 0),   // End at 5:00 PM (9 hours)
            TicketNr = null,
            Text = description,
            Source = GetSourceFromCategory(category),
            Category = category
        };

        Log.Information("Created special whole-day {Category} timeslot for {Date}", category, date);
        return [timeSlot];
    }

    /// <summary>
    /// Get ticket number from a list of events, with fallback to branch active at specified time
    /// </summary>
    private string? GetTicketNumberFromEvents(List<Event> events, DateTime slotTime, DateOnly date, List<(DateTime Time, string Branch, string? Ticket)> branchHistory)
    {
        // First try to find ticket in events
        var eventWithTicket = events.FirstOrDefault(e => GetTicketNumber(e) != null);
        var ticket = GetTicketNumber(eventWithTicket);

        if (string.IsNullOrEmpty(ticket))
        {
            // No ticket in events, use branch that was active at this time
            ticket = GetTicketForTimeFromBranchHistory(slotTime, branchHistory);

            if (!string.IsNullOrEmpty(ticket))
            {
                Log.Debug("No ticket in {Count} events at {Time}, using branch ticket {Ticket}", 
                    events.Count, slotTime, ticket);
            }
        }

        return ticket;
    }

    /// <summary>
    /// Generate description for time slot based on events
    /// </summary>
    private string GetWorkDescription(List<Event> workEvents)
    {
        if (workEvents.Count == 0)
        {
            return "Work";
        }

        // Filter out lunch break and other skeleton events - we only want actual work events
        var actualWorkEvents = workEvents
            .Where(e => e.EventType != "Lunch Break Start" && 
                       e.EventType != "Lunch Break End" &&
                       e.EventType != "Boot" &&
                       e.EventType != "Shutdown" &&
                       e.EventType != "CalendarMeetingStart" &&
                       e.EventType != "CalendarMeetingEnd")
            .ToList();

        if (actualWorkEvents.Count == 0)
        {
            return "Work";
        }

        // Prefer commit messages
        var commits = actualWorkEvents.Where(e => e.EventType.Contains("Commit", StringComparison.OrdinalIgnoreCase)).ToList();
        if (commits.Any())
        {
            // Use the most recent or most detailed commit message
            var bestCommit = commits
                .OrderByDescending(e => e.Description?.Length ?? 0)
                .First();
            return bestCommit.Description ?? bestCommit.EventType;
        }

        // Fall back to first actual work event's description
        var firstEvent = actualWorkEvents[0];
        return !string.IsNullOrWhiteSpace(firstEvent.Description) 
            ? firstEvent.Description 
            : firstEvent.EventType;
    }
}
