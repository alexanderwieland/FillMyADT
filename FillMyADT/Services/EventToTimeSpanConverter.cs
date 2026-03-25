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
    private static readonly ILogger Log = Serilog.Log.ForContext<EventToTimeSpanConverter>();

    private readonly GitEventSource? _gitEventSource;
    private readonly AppConfiguration? _appConfig;

    private const int StandardRoundingMinutes = 15;
    private const int FirstSlotRoundingMinutes = 5;
    private const int LastSlotRoundingMinutes = 5;

    // Lunch break rules
    private static readonly TimeOnly BreakWindowStart = new(11, 30);
    private static readonly TimeOnly BreakWindowEnd = new(14, 0);
    private const int MinBreakMinutes = 30;
    private const int MaxBreakMinutes = 60;
    private static readonly TimeOnly DefaultBreakStart = new(11, 30);
    private static readonly TimeOnly DefaultBreakEnd = new(12, 00);

    public EventToTimeSpanConverter(GitEventSource? gitEventSource = null, AppConfiguration? appConfig = null)
    {
        _gitEventSource = gitEventSource;
        _appConfig = appConfig;
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
                .Where(e => e.EventType == EventType.BranchSwitch)
                .OrderBy(e => e.Timestamp)
                .ToList();

            foreach (var switchEvent in branchSwitches)
            {
                if (switchEvent.Metadata != null && switchEvent.Metadata.TryGetValue("Branch", out var branchName))
                {
                    var ticket = switchEvent.Metadata.TryGetValue("TicketNumber", out var t) ? t : TicketParser.FromGitText(branchName);
                    branchHistory.Add((switchEvent.Timestamp, branchName, ticket));
                    Log.Debug("Branch switch at {Time}: {Branch} (Ticket: {Ticket})",
                        switchEvent.Timestamp, branchName, ticket ?? "none");
                }
            }

            // If no branch switches found, resolve the branch active on the processed day via reflog.
            // This ensures filler slots get the ticket of the branch that was checked out that day,
            // not whatever is currently checked out right now.
            if (branchHistory.Count == 0)
            {
                // Use the midpoint of the day being processed (determined from events timestamps)
                var firstEventTime = events.Min(e => e.Timestamp);
                var midDay = firstEventTime.Date.AddHours(12);

                var dayBranch = await _gitEventSource.GetBranchAtTimeAsync(midDay, cancellationToken);
                if (!string.IsNullOrEmpty(dayBranch))
                {
                    var ticket = TicketParser.FromGitText(dayBranch);
                    branchHistory.Add((firstEventTime.Date, dayBranch, ticket));
                    Log.Information("No branch switches found for {Date}; using branch active at midday: {Branch} (Ticket: {Ticket})",
                        firstEventTime.Date.ToShortDateString(), dayBranch, ticket ?? "none");
                }
                else
                {
                    var currentBranch = await _gitEventSource.GetCurrentBranchAsync(cancellationToken);
                    if (!string.IsNullOrEmpty(currentBranch))
                    {
                        var ticket = TicketParser.FromGitText(currentBranch);
                        branchHistory.Add((DateTime.MinValue, currentBranch, ticket));
                        Log.Information("Could not resolve branch for that day; falling back to current branch: {Branch} (Ticket: {Ticket})",
                            currentBranch, ticket ?? "none");
                    }
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

        // Check for Homeoffice day (handled differently - sets location on all slots)
        var homeofficeEvent = sortedEvents.FirstOrDefault(e => e.EventType == EventType.SpecialWholeDayHomeoffice);
        bool isHomeofficeDay = homeofficeEvent != null;

        // Check for special whole-day events that override all slots (Holiday or Zeitausgleich)
        var specialEvent = sortedEvents.FirstOrDefault(e =>
            e.EventType == EventType.SpecialWholeDayHoliday ||
            e.EventType == EventType.SpecialWholeDayZeitausgleich);

        if (specialEvent != null)
        {
            // Return a single special timeslot for the whole day
            return CreateSpecialWholeDayTimeSlot(specialEvent);
        }

        // Find Boot and Shutdown events to determine work boundaries
        var bootEvent = sortedEvents.FirstOrDefault(e => e.EventType == EventType.Boot);
        var shutdownEvent = sortedEvents.LastOrDefault(e => e.EventType == EventType.Shutdown);

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
            .Where(e => e.EventType != EventType.Boot && e.EventType != EventType.Shutdown
                     && e.EventType != EventType.CalendarMeetingStart && e.EventType != EventType.CalendarMeetingEnd
                     && e.EventType != EventType.TicketStart && e.EventType != EventType.TicketEnd
                     && e.EventType != EventType.ReviewStart && e.EventType != EventType.ReviewEnd)
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

        // Automatically merge consecutive work slots that share the same ticket number
        timeSlots = MergeConsecutiveSameTicketSlots(timeSlots);

        // Apply location to all slots based on homeoffice day or default
        var defaultLocation = _appConfig?.DefaultLocation == "WV" ? Location.WV : Location.HOME;
        var location = isHomeofficeDay ? Location.HOME : defaultLocation;

        // Update timeslots with location
        foreach (var slot in timeSlots)
        {
            slot.Location = location;
        }

        if (isHomeofficeDay)
        {
            Log.Information("Applied Home location to all {SlotCount} time slots for Homeoffice day", timeSlots.Count);
        }

        Log.Information("Converted {EventCount} events into {SlotCount} time slots", events.Count, timeSlots.Count);
        return timeSlots;
    }

    private record CalendarMeeting(TimeOnly Start, TimeOnly End, string Subject, TimeSlotCategory Category, string? TicketNumber = null);

    /// <summary>
    /// Extract and consolidate overlapping calendar meetings including lunch break
    /// </summary>
    private List<CalendarMeeting> ExtractCalendarMeetings(List<Event> events, TimeOnly workStart, TimeOnly workEnd)
    {
        var meetingStarts = events
            .Where(e => e.EventType == EventType.CalendarMeetingStart || e.EventType == EventType.ReviewStart || e.EventType == EventType.TicketStart)
            .Where(e => TimeOnly.FromDateTime(e.Timestamp) < workEnd)
            .ToList();

        var meetings = new List<CalendarMeeting>();

        // Add lunch break as a meeting if present
        var lunchStart = events.FirstOrDefault(e => e.EventType == EventType.LunchBreakStart);
        var lunchEnd = events.FirstOrDefault(e => e.EventType == EventType.LunchBreakEnd);

        if (lunchStart != null && lunchEnd != null)
        {
            var lunchStartTime = TimeOnly.FromDateTime(lunchStart.Timestamp);
            var lunchEndTime = TimeOnly.FromDateTime(lunchEnd.Timestamp);

            // Only add lunch if it's within work hours
            if (lunchStartTime >= workStart && lunchEndTime <= workEnd)
            {
                meetings.Add(new CalendarMeeting(lunchStartTime, lunchStartTime.AddMinutes(MinBreakMinutes), "Lunch Break", TimeSlotCategory.Break));
                Log.Debug("Added lunch break to schedule: {Start} - {End}", lunchStartTime, lunchEndTime);
            }
        }

        foreach (var startEvent in meetingStarts)
        {
            // Match corresponding end event (CalendarMeetingEnd, ReviewEnd, or TicketEnd)
            var expectedEndType = startEvent.EventType == EventType.ReviewStart ? EventType.ReviewEnd :
                                  startEvent.EventType == EventType.TicketStart ? EventType.TicketEnd :
                                  EventType.CalendarMeetingEnd;

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

            var ticketNumber = startEvent.Metadata?.GetValueOrDefault("TicketNumber");
            meetings.Add(new CalendarMeeting(start, end, startEvent.Description ?? "Meeting", TimeSlotCategoryExtensions.ParseEventType(startEvent.EventType), ticketNumber));
        }

        // Handle overlaps by adjusting times
        meetings = ResolveOverlappingMeetings(meetings);

        return meetings.OrderBy(m => m.Start).ToList();
    }

    /// <summary>
    /// Resolve overlapping meetings. Meeting start/end times are never modified.
    /// When a break overlaps a regular meeting, the break's start is shifted forward (after the
    /// meeting ends) or backward (before the next meeting starts) so it always occupies exactly
    /// MinBreakMinutes. Regular meetings that overlap each other have their start pushed forward.
    /// </summary>
    private List<CalendarMeeting> ResolveOverlappingMeetings(List<CalendarMeeting> meetings)
    {
        if (meetings.Count <= 1)
            return meetings;

        var sorted = meetings.OrderBy(m => m.Start).ToList();

        // First pass: reposition the break so it fits between surrounding meetings
        for (var i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].Category != TimeSlotCategory.Break)
                continue;

            var breakMeeting = sorted[i];
            var breakStart = breakMeeting.Start;

            // If the preceding meeting overlaps, push the break forward to after it ends
            if (i > 0 && sorted[i - 1].End > breakStart)
            {
                breakStart = sorted[i - 1].End;
                Log.Debug("Break pushed forward to {NewStart} due to overlap with {Subject}",
                    breakStart, sorted[i - 1].Subject);
            }

            // If the following meeting starts before the break would end, push the break backward
            var breakEnd = breakStart.AddMinutes(MinBreakMinutes);
            if (i < sorted.Count - 1 && sorted[i + 1].Start < breakEnd)
            {
                breakStart = sorted[i + 1].Start.AddMinutes(-MinBreakMinutes);
                Log.Debug("Break pushed backward to {NewStart} due to overlap with {Subject}",
                    breakStart, sorted[i + 1].Subject);
            }

            // Clamp within the allowed break window
            breakStart = breakStart < BreakWindowStart ? BreakWindowStart : breakStart;
            var maxStart = BreakWindowEnd.AddMinutes(-MinBreakMinutes);
            breakStart = breakStart > maxStart ? maxStart : breakStart;

            if (breakStart != breakMeeting.Start)
                sorted[i] = new CalendarMeeting(breakStart, breakStart.AddMinutes(MinBreakMinutes), breakMeeting.Subject, breakMeeting.Category, breakMeeting.TicketNumber);
        }

        // Second pass: resolve any remaining meeting-vs-meeting overlaps by pushing starts forward
        var resolved = new List<CalendarMeeting>();
        var previousEnd = TimeOnly.MinValue;

        foreach (var current in sorted)
        {
            var isBreak = current.Category == TimeSlotCategory.Break;

            // Breaks keep their repositioned start; regular meetings are pushed past the previous end
            var start = (!isBreak && current.Start < previousEnd) ? previousEnd : current.Start;
            var end = isBreak ? current.End : current.End;

            if (end <= start)
            {
                Log.Debug("Skipping {Subject}: fully overlapped by previous slot", current.Subject);
                continue;
            }

            if (start != current.Start)
            {
                Log.Debug("Adjusted {Subject} start: {OldStart} → {NewStart}",
                    current.Subject, current.Start, start);
            }

            resolved.Add(new CalendarMeeting(start, end, current.Subject, current.Category, current.TicketNumber));
            previousEnd = end;
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
                return (gapStart, gapStart.AddMinutes(MinBreakMinutes));
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
                        Location = Location.WV,
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
                    Location = Location.WV,
                    Source = GetSourceFromCategory(TimeSlotCategory.Work),
                    Category = TimeSlotCategory.Work,
                    Metadata = GetMetadataFromEvents(eventsInPeriod.Any() ? eventsInPeriod : workEvents, branchHistory, slotTime)
                });
            }

            // Add meeting slot with appropriate category
            var category = meeting.Category;
            timeSlots.Add(new TimeSlot
            {
                Date = date,
                StartTime = meeting.Start,
                EndTime = meeting.End,
                TicketNr = meeting.TicketNumber,
                Text = meeting.Subject,
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
                    Location = Location.WV,
                    Source = GetSourceFromCategory(TimeSlotCategory.Work),
                    Category = TimeSlotCategory.Work,
                    Metadata = GetMetadataFromEvents(eventsBeforeLunch.Any() ? eventsBeforeLunch : workEvents, branchHistory, slotTime)
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
                Location = Location.WV,
                Source = GetSourceFromCategory(TimeSlotCategory.Work),
                Category = TimeSlotCategory.Work,
                Metadata = GetMetadataFromEvents(eventsInPeriod.Any() ? eventsInPeriod : workEvents, branchHistory, slotTime)
            });
        }
    }

    /// <summary>
    /// Returns true for categories produced by work-producing sources (Git and Edge Browser).
    /// These are the only slot types eligible for consecutive merging.
    /// </summary>
    private static bool IsWorkLikeCategory(TimeSlotCategory category) =>
        category is TimeSlotCategory.Work
                 or TimeSlotCategory.RedmineTickets
                 or TimeSlotCategory.TfsWork;

    /// <summary>
    /// Merge consecutive work/browser slots that directly follow each other and share the same
    /// ticket number. Any other slot type (meeting, break, startup) breaks the chain.
    /// </summary>
    private List<TimeSlot> MergeConsecutiveSameTicketSlots(List<TimeSlot> timeSlots)
    {
        if (timeSlots.Count <= 1)
            return timeSlots;

        var result = new List<TimeSlot>(timeSlots.Count);
        var i = 0;

        while (i < timeSlots.Count)
        {
            var current = timeSlots[i];

            if (IsWorkLikeCategory(current.Category) && !string.IsNullOrEmpty(current.TicketNr))
            {
                // Extend the run as long as the next slot is also work-like with the same ticket
                var j = i + 1;
                while (j < timeSlots.Count
                    && IsWorkLikeCategory(timeSlots[j].Category)
                    && timeSlots[j].TicketNr == current.TicketNr)
                {
                    j++;
                }

                if (j > i + 1)
                {
                    var run = timeSlots[i..j];
                    var first = run[0];
                    var last = run[^1];

                    // Prefer the Git slot as the base so its Category, Source and Metadata win.
                    // Fall back to the first slot when the run contains no Git slot.
                    var gitSlot = run.FirstOrDefault(s => s.Category == TimeSlotCategory.Work) ?? first;

                    var merged = gitSlot with
                    {
                        StartTime = first.StartTime,
                        EndTime = last.EndTime,
                        Text = run
                            .Select(s => s.Text)
                            .Where(t => !string.IsNullOrWhiteSpace(t) && t != "Work")
                            .OrderByDescending(t => t!.Length)
                            .FirstOrDefault() ?? "Work"
                    };

                    Log.Debug("Merged {Count} consecutive work slots with ticket {Ticket} into {Start}-{End}",
                        run.Count, current.TicketNr, first.StartTime, last.EndTime);

                    result.Add(merged);
                    i = j;
                    continue;
                }
            }

            result.Add(current);
            i++;
        }

        return result;
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
                    gapStart, gapStart.AddMinutes(MinBreakMinutes), gapDuration);
                return (gapStart, gapStart.AddMinutes(MinBreakMinutes));
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
                    gapStart, gapStart.AddMinutes(MinBreakMinutes), gapDuration);
                return (gapStart, gapStart.AddMinutes(MinBreakMinutes));
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
                    gapStart, gapStart.AddMinutes(MinBreakMinutes), gapDuration);
                return (gapStart, gapStart.AddMinutes(MinBreakMinutes));
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
        TimeSlotCategory.Urlaub => "Outlook",
        TimeSlotCategory.Zeitausgleich => "Outlook",
        TimeSlotCategory.Other => "Unknown",
        _ => "Unknown"
    };

    /// <summary>
    /// Create a single special whole-day timeslot for Holiday or Zeitausgleich
    /// </summary>
    private IReadOnlyList<TimeSlot> CreateSpecialWholeDayTimeSlot(Event specialEvent)
    {
        var date = DateOnly.FromDateTime(specialEvent.Timestamp);
        var category = specialEvent.EventType switch
        {
            EventType.SpecialWholeDayHoliday => TimeSlotCategory.Urlaub,
            EventType.SpecialWholeDayZeitausgleich => TimeSlotCategory.Zeitausgleich,
            _ => TimeSlotCategory.Other
        };

        var description = specialEvent.Description ?? category switch
        {
            TimeSlotCategory.Urlaub => "Holiday",
            TimeSlotCategory.Zeitausgleich => "Zeitausgleich",
            _ => "Special Day"
        };

        // Calculate start and end times based on category
        TimeOnly startTime;
        TimeOnly endTime;

        if (category == TimeSlotCategory.Urlaub)
        {
            // Holiday: configurable work hours (default 6.4h = 6h 24min)
            startTime = new TimeOnly(8, 0);
            var holidayHours = _appConfig?.WorkHours ?? 6.4;
            var totalMinutes = (int)(holidayHours * 60);
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;
            endTime = startTime.AddHours(hours).AddMinutes(minutes);
        }
        else if (category == TimeSlotCategory.Zeitausgleich)
        {
            // Zeitausgleich: 00:00 - 00:00 (no hours)
            startTime = new TimeOnly(0, 0);
            endTime = new TimeOnly(0, 0);
        }
        else
        {
            // Fallback
            startTime = new TimeOnly(8, 0);
            endTime = new TimeOnly(17, 0);
        }

        var timeSlot = new TimeSlot
        {
            Date = date,
            StartTime = startTime,
            EndTime = endTime,
            TicketNr = null,
            Text = description,
            Source = GetSourceFromCategory(category),
            Category = category
        };

        Log.Information("Created special whole-day {Category} timeslot for {Date} ({Start} - {End})",
            category, date, startTime, endTime);
        return [timeSlot];
    }

    /// <summary>
    /// Get ticket number from a list of events, with fallback to branch active at specified time
    /// </summary>
    private string? GetTicketNumberFromEvents(List<Event> events, DateTime slotTime, DateOnly date, List<(DateTime Time, string Branch, string? Ticket)> branchHistory)
    {
        // Prefer ticket from commit events (more specific than branch-level tickets)
        var commitWithTicket = events
            .Where(e => e.EventType == EventType.Commit)
            .FirstOrDefault(e => GetTicketNumber(e) != null);

        var ticket = GetTicketNumber(commitWithTicket);

        // Fallback to any event with a ticket (including branch switches)
        if (string.IsNullOrEmpty(ticket))
        {
            var eventWithTicket = events.FirstOrDefault(e => GetTicketNumber(e) != null);
            ticket = GetTicketNumber(eventWithTicket);
        }

        // Last resort: use branch that was active at this time
        if (string.IsNullOrEmpty(ticket))
        {
            ticket = GetTicketForTimeFromBranchHistory(slotTime, branchHistory);

            if (!string.IsNullOrEmpty(ticket))
            {
                Log.Debug("No ticket in {Count} events at {Time}, using branch ticket {Ticket}",
                    events.Count, slotTime, ticket);
            }
        }

        return TicketParser.EnsureHashPrefix(ticket);
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
            .Where(e => e.EventType != EventType.LunchBreakStart &&
                       e.EventType != EventType.LunchBreakEnd &&
                       e.EventType != EventType.Boot &&
                       e.EventType != EventType.Shutdown &&
                       e.EventType != EventType.CalendarMeetingStart &&
                       e.EventType != EventType.CalendarMeetingEnd &&
                       e.EventType != EventType.TicketStart &&
                       e.EventType != EventType.TicketEnd)
            .ToList();

        if (actualWorkEvents.Count == 0)
        {
            return "Work";
        }

        // Prefer commit messages
        var commits = actualWorkEvents.Where(e => e.EventType == EventType.Commit).ToList();
        if (commits.Any())
        {
            // Use the most recent or most detailed commit message
            var bestCommit = commits
                .OrderByDescending(e => e.Description?.Length ?? 0)
                .First();
            return bestCommit.Description ?? bestCommit.EventType.ToString();
        }

        // Fall back to first actual work event's description
        var firstEvent = actualWorkEvents[0];
        return !string.IsNullOrWhiteSpace(firstEvent.Description)
            ? firstEvent.Description
            : firstEvent.EventType.ToString();
    }

    /// <summary>
    /// Extract metadata from work events (Repository, Branch, etc.)
    /// </summary>
    private Dictionary<string, string>? GetMetadataFromEvents(List<Event> workEvents, List<(DateTime Time, string Branch, string? Ticket)> branchHistory, DateTime slotTime)
    {
        if (workEvents.Count == 0)
            return null;

        var metadata = new Dictionary<string, string>();

        // Prefer metadata from Git commit events
        var gitEvent = workEvents.FirstOrDefault(e => e.EventType == EventType.Commit || e.EventType == EventType.BranchSwitch);

        if (gitEvent?.Metadata != null)
        {
            // Copy Repository
            if (gitEvent.Metadata.TryGetValue("Repository", out var repository))
            {
                metadata["Repository"] = repository;
            }

            // Copy Branch if available
            if (gitEvent.Metadata.TryGetValue("Branch", out var branch))
            {
                metadata["Branch"] = branch;
            }
        }

        // If no branch in events, get from branch history
        if (!metadata.ContainsKey("Branch") && branchHistory.Count > 0)
        {
            var activeBranch = branchHistory
                .Where(b => b.Time <= slotTime)
                .OrderByDescending(b => b.Time)
                .FirstOrDefault();

            if (activeBranch != default && !string.IsNullOrEmpty(activeBranch.Branch))
            {
                metadata["Branch"] = activeBranch.Branch;
            }
        }

        return metadata.Count > 0 ? metadata : null;
    }
}
