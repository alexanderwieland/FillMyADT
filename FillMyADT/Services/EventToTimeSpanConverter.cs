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

                Log.Information("BuildBranchHistory: no branch switches in events for {Date}, querying default repo reflog at midday {MidDay}",
                    firstEventTime.Date.ToShortDateString(), midDay);

                var dayBranch = await _gitEventSource.GetBranchAtTimeAsync(midDay, cancellationToken);
                if (!string.IsNullOrEmpty(dayBranch))
                {
                    var ticket = TicketParser.FromGitText(dayBranch);

                    // Branch name has no ticket (e.g. "aw/filter_rework_2") —
                    // scan all commit/rebase messages on that day for one.
                    if (string.IsNullOrEmpty(ticket))
                    {
                        Log.Information("BuildBranchHistory: branch '{Branch}' has no ticket in its name; scanning day reflog for ticket",
                            dayBranch);
                        ticket = await _gitEventSource.GetTicketFromDayReflogAsync(firstEventTime.Date, cancellationToken);
                    }

                    branchHistory.Add((firstEventTime.Date, dayBranch, ticket));
                    Log.Information("BuildBranchHistory: default branch for {Date} is '{Branch}', ticket: {Ticket}",
                        firstEventTime.Date.ToShortDateString(), dayBranch, ticket ?? "(none)");
                }
                else
                {
                    var currentBranch = await _gitEventSource.GetCurrentBranchAsync(cancellationToken);
                    if (!string.IsNullOrEmpty(currentBranch))
                    {
                        var ticket = TicketParser.FromGitText(currentBranch);
                        branchHistory.Add((DateTime.MinValue, currentBranch, ticket));
                        Log.Warning("BuildBranchHistory: could not resolve branch for {Date} via reflog; using current branch '{Branch}', ticket: {Ticket}",
                            firstEventTime.Date.ToShortDateString(), currentBranch, ticket ?? "(none)");
                    }
                    else
                    {
                        Log.Warning("BuildBranchHistory: no branch resolved at all for {Date} — filler slots will have no ticket",
                            firstEventTime.Date.ToShortDateString());
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

        // Only use events of this day; times are compared as TimeOnly below, so events from other
        // days would otherwise leak in. End events at/after midnight are kept to close meetings.
        sortedEvents = sortedEvents
            .Where(e => DateOnly.FromDateTime(e.Timestamp) == date
                        || (e.EventType is EventType.CalendarMeetingEnd or EventType.ReviewEnd or EventType.TicketEnd
                            && DateOnly.FromDateTime(e.Timestamp) == date.AddDays(1)))
            .ToList();
        shutdownEvent = sortedEvents.LastOrDefault(e => e.EventType == EventType.Shutdown);

        // First slot: boot rounded up to 5min, until next quarter hour
        var firstSlotStart = TimeHelper.RoundUp(TimeOnly.FromDateTime(bootEvent.Timestamp), FirstSlotRoundingMinutes);
        var firstSlotEnd = TimeHelper.RoundUp(firstSlotStart, StandardRoundingMinutes);

        // If first slot would be 0 duration, extend to next quarter (15 min minimum)
        if (firstSlotStart == firstSlotEnd)
        {
            firstSlotEnd = firstSlotEnd.AddMinutes(StandardRoundingMinutes);
        }

        // Work slots start after first slot
        var workStart = firstSlotEnd;
        var workEnd = shutdownEvent != null
            ? TimeHelper.RoundDown(TimeOnly.FromDateTime(shutdownEvent.Timestamp), LastSlotRoundingMinutes)
            : new TimeOnly(22, 0); // Default to 10 PM if no shutdown

		// Meetings, browser ticket blocks and the lunch break are fixed blocks; work slots fill the gaps between them
		var fixedBlocks = ExtractCalendarMeetings(sortedEvents, workStart, workEnd);

		var workEvents = sortedEvents
			.Where(e => e.EventType is not (EventType.Boot or EventType.Shutdown
				or EventType.CalendarMeetingStart or EventType.CalendarMeetingEnd
				or EventType.TicketStart or EventType.TicketEnd
				or EventType.ReviewStart or EventType.ReviewEnd
				or EventType.LunchBreakStart or EventType.LunchBreakEnd))
			.Where(e => TimeOnly.FromDateTime(e.Timestamp) >= workStart &&
					   TimeOnly.FromDateTime(e.Timestamp) <= workEnd)
			.ToList();

		if (!fixedBlocks.Any(m => m.Category == TimeSlotCategory.Break)
			&& workStart < BreakWindowEnd && workEnd > BreakWindowStart)
		{
			var (breakStart, breakEnd) = DetectLunchBreak(fixedBlocks, workEvents);
			if (breakStart < workStart) breakStart = workStart;
			if (breakEnd > workEnd) breakEnd = workEnd;
			if (breakStart < breakEnd)
			{
				fixedBlocks = ResolveOverlappingMeetings(
					[.. fixedBlocks, new CalendarMeeting(breakStart, breakEnd, "Lunch Break", TimeSlotCategory.Break)]);
			}
		}

		var timeSlots = new List<TimeSlot>
		{
			new()
			{
				Date = date,
				StartTime = firstSlotStart,
				EndTime = firstSlotEnd,
				Text = "Startup",
				Source = GetSourceFromCategory(TimeSlotCategory.Startup),
				Category = TimeSlotCategory.Startup
			}
		};

		var currentTime = workStart;
		foreach (var block in fixedBlocks.OrderBy(b => b.Start))
		{
			var blockStart = block.Start < currentTime ? currentTime : block.Start;
			if (blockStart >= block.End)
				continue;

			if (currentTime < blockStart)
				timeSlots.Add(CreateWorkSlot(date, currentTime, blockStart, workEvents, branchHistory));

			timeSlots.Add(new TimeSlot
			{
				Date = date,
				StartTime = blockStart,
				EndTime = block.End,
				TicketNr = block.TicketNumber,
				Text = block.Subject,
				Source = GetSourceFromCategory(block.Category),
				Category = block.Category
			});
			currentTime = block.End;
		}

		if (currentTime < workEnd)
			timeSlots.Add(CreateWorkSlot(date, currentTime, workEnd, workEvents, branchHistory));

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
                var minEnd = lunchStartTime.AddMinutes(MinBreakMinutes);
                meetings.Add(new CalendarMeeting(lunchStartTime, lunchEndTime > minEnd ? lunchEndTime : minEnd, "Lunch Break", TimeSlotCategory.Break));
                Log.Debug("Added lunch break to schedule: {Start} - {End}", lunchStartTime, lunchEndTime);
            }
        }

        var usedEnds = new HashSet<Event>(ReferenceEqualityComparer.Instance);
        foreach (var startEvent in meetingStarts)
        {
            // Match corresponding
            var expectedEndType = startEvent.EventType == EventType.ReviewStart ? EventType.ReviewEnd :
                                  startEvent.EventType == EventType.TicketStart ? EventType.TicketEnd :
                                  EventType.CalendarMeetingEnd;

            // Each end event may only close one start (same-subject meetings can overlap/recur)
            var endEvent = events
                .Where(e => e.EventType == expectedEndType &&
                            e.Description == startEvent.Description &&
                            e.Timestamp >= startEvent.Timestamp &&
                            !usedEnds.Contains(e))
                .MinBy(e => e.Timestamp);

            if (endEvent == null)
                continue;
            usedEnds.Add(endEvent);

            var start = TimeOnly.FromDateTime(startEvent.Timestamp);
            var end = TimeOnly.FromDateTime(endEvent.Timestamp);

            // Clip to work hours
            if (end <= workStart || start >= workEnd)
                continue;

            // Midnight-crossing end (e.g. 00:00) means "until end of day"
            if (endEvent.Timestamp.Date > startEvent.Timestamp.Date)
                end = workEnd;
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
    /// Place the break at the free spot closest to its detected start inside the break window,
    /// keeping its duration (shrinking to MinBreakMinutes if needed). Kept unchanged if nothing fits.
    /// </summary>
    private static CalendarMeeting FitBreak(CalendarMeeting brk, List<CalendarMeeting> others)
    {
        var preferred = Math.Max(MinBreakMinutes, (int)(brk.End - brk.Start).TotalMinutes);
        var blocks = others
            .Where(m => !IsFlexibleTicketSlot(m.Category) && m.End > BreakWindowStart && m.Start < BreakWindowEnd)
            .OrderBy(m => m.Start)
            .ToList();

        // Free gaps inside the break window
        var gaps = new List<(TimeOnly Start, TimeOnly End)>();
        var cursor = BreakWindowStart;
        foreach (var b in blocks)
        {
            if (b.Start > cursor)
                gaps.Add((cursor, b.Start));
            if (b.End > cursor)
                cursor = b.End;
        }
        if (cursor < BreakWindowEnd)
            gaps.Add((cursor, BreakWindowEnd));

        foreach (var minutes in new[] { preferred, MinBreakMinutes }.Distinct())
        {
            var best = gaps
                .Where(g => (g.End - g.Start).TotalMinutes >= minutes)
                .Select(g =>
                {
                    var latestStart = g.End.AddMinutes(-minutes);
                    var start = brk.Start < g.Start ? g.Start : brk.Start > latestStart ? latestStart : brk.Start;
                    return (Start: start, Distance: Math.Abs((start - brk.Start).TotalMinutes));
                })
                .OrderBy(c => c.Distance)
                .Select(c => (TimeOnly?)c.Start)
                .FirstOrDefault();

            if (best is { } s)
            {
                if (s != brk.Start || minutes != (int)(brk.End - brk.Start).TotalMinutes)
                    Log.Debug("Break moved from {OldStart} to {NewStart} ({Minutes} min) to avoid overlaps", brk.Start, s, minutes);
                return brk with { Start = s, End = s.AddMinutes(minutes) };
            }
        }

        Log.Warning("No free {Minutes}-minute gap for the break between {From} and {To}; keeping it at {Start}",
            MinBreakMinutes, BreakWindowStart, BreakWindowEnd, brk.Start);
        return brk;
    }

    /// <summary>
    /// Returns true for categories that represent browser-derived ticket slots
    /// These are flexible and must yield to fixed Outlook calendar meetings.
    /// </summary>
    private static bool IsFlexibleTicketSlot(TimeSlotCategory category) =>
        category is TimeSlotCategory.RedmineTickets or TimeSlotCategory.TfsWork;

    /// <summary>
    /// Resolve overlapping meetings. Meeting start/end times are never modified.
    /// When a break overlaps a regular meeting, the break's start is shifted forward (after the
    /// meeting ends) or backward (before the next meeting starts) so it always occupies exactly
    /// MinBreakMinutes. Regular meetings that overlap each other have their start pushed forward.
    /// Flexible ticket slots (Redmine/TFS) are truncated when they overlap a fixed Outlook meeting,
    /// never the other way around.
    /// </summary>
    private List<CalendarMeeting> ResolveOverlappingMeetings(List<CalendarMeeting> meetings)
    {
        if (meetings.Count <= 1)
            return meetings;

        var sorted = meetings.OrderBy(m => m.Start).ToList();

        // First pass: move the break into a free gap that does not overlap any other block
        var breakIdx = sorted.FindIndex(m => m.Category == TimeSlotCategory.Break);
        if (breakIdx >= 0)
            sorted[breakIdx] = FitBreak(sorted[breakIdx], sorted.Where((_, i) => i != breakIdx).ToList());

        // Second pass: resolve any remaining meeting-vs-meeting overlaps.
        // Priority rules:
        //   1. Fixed Outlook meetings (Meeting/Break) are immovable — their times never change.
        //   2. Flexible ticket slots (Redmine/TFS) are capped at the start of the next fixed meeting
        //      and truncated/dropped when they overlap with it.
        //   3. Other slots are pushed past the previous end as a safe fallback.
        var resolved = new List<CalendarMeeting>();
        var previousEnd = TimeOnly.MinValue;

        for (var idx = 0; idx < sorted.Count; idx++)
        {
            var current = sorted[idx];
            var isBreak = current.Category == TimeSlotCategory.Break;
            var isFlexible = IsFlexibleTicketSlot(current.Category);

            TimeOnly start;
            TimeOnly end;

            if (isBreak)
            {
                // Breaks keep their repositioned start from the first pass
                start = current.Start;
                end = current.End;
            }
            else if (isFlexible)
            {
                // Flexible ticket slot: start after the previous slot, but never push into
                // the next fixed (Outlook) meeting — cap end at that meeting's start instead.
                start = current.Start < previousEnd ? previousEnd : current.Start;
                end = current.End;

                // Look ahead: find the nearest following fixed meeting that this slot would overlap
                var nextFixed = sorted
                    .Skip(idx + 1)
                    .FirstOrDefault(m => !IsFlexibleTicketSlot(m.Category) && m.Category != TimeSlotCategory.Break);

                if (nextFixed != null && end > nextFixed.Start)
                {
                    end = nextFixed.Start;
                    Log.Debug("Capped flexible ticket slot {Subject} end at {Cap} to protect fixed meeting '{Next}'",
                        current.Subject, end, nextFixed.Subject);
                }

                if (end <= start)
                {
                    Log.Debug("Dropping flexible ticket slot {Subject}: no room before next fixed meeting",
                        current.Subject);
                    continue;
                }
            }
            else
            {
                // Fixed meetings are immovable relative to external events, but when two fixed
                // meetings overlap each other the earlier one is truncated at the later one's start.
                start = current.Start;
                end = current.End;

                // Look ahead: if the next fixed meeting starts before this one ends, truncate here
                var nextFixed = sorted
                    .Skip(idx + 1)
                    .FirstOrDefault(m => !IsFlexibleTicketSlot(m.Category) && m.Category != TimeSlotCategory.Break);

                if (nextFixed != null && end > nextFixed.Start)
                {
                    Log.Debug("Truncating fixed meeting '{Subject}' end from {OldEnd} to {NewEnd} due to overlap with '{Next}'",
                        current.Subject, end, nextFixed.Start, nextFixed.Subject);
                    end = nextFixed.Start;
                }
            }

            if (end <= start)
            {
                Log.Debug("Skipping {Subject}: fully overlapped by previous slot", current.Subject);
                continue;
            }

            resolved.Add(new CalendarMeeting(start, end, current.Subject, current.Category, current.TicketNumber));
            previousEnd = end;
        }

        return resolved;
    }

	/// <summary>
	/// Create a work slot for a gap. Ticket priority: events inside the slot, then the branch active
	/// at slot start, then any event of the day.
	/// </summary>
	private TimeSlot CreateWorkSlot(DateOnly date, TimeOnly start, TimeOnly end, List<Event> workEvents, List<(DateTime Time, string Branch, string? Ticket)> branchHistory)
	{
		var slotEvents = workEvents
			.Where(e => TimeOnly.FromDateTime(e.Timestamp) >= start && TimeOnly.FromDateTime(e.Timestamp) < end)
			.ToList();
		var slotTime = date.ToDateTime(start);

		// Branches/commits are often created after the work started, so a later ticket of the same day
		// is a better match than the branch that happened to be checked out at slot time.
		var laterEvents = workEvents.Where(e => TimeOnly.FromDateTime(e.Timestamp) >= end).OrderBy(e => e.Timestamp).ToList();
		var ticket = GetTicketFromEvents(slotEvents)
			?? laterEvents.Select(e => GetTicketFromEvents([e])).FirstOrDefault(t => t != null)
			?? GetTicketForTimeFromBranchHistory(slotTime, branchHistory)
			?? GetTicketFromEvents(workEvents);

		return new TimeSlot
		{
			Date = date,
			StartTime = start,
			EndTime = end,
			TicketNr = TicketParser.EnsureHashPrefix(ticket),
			Text = GetWorkDescription(slotEvents.Count > 0 ? slotEvents : workEvents),
			Source = GetSourceFromCategory(TimeSlotCategory.Work),
			Category = TimeSlotCategory.Work,
			Metadata = GetMetadataFromEvents(slotEvents, branchHistory, slotTime)
		};
	}

	/// <summary>
	/// Pick a lunch break: from gaps between meetings in the break window if there are any,
	/// otherwise from a gap between work events, otherwise the default.
	/// </summary>
	private static (TimeOnly Start, TimeOnly End) DetectLunchBreak(List<CalendarMeeting> meetings, List<Event> workEvents)
	{
		var meetingsInWindow = meetings
			.Where(m => m.End > BreakWindowStart && m.Start < BreakWindowEnd)
			.OrderBy(m => m.Start)
			.ToList();

		var detected = meetingsInWindow.Count > 0
			? FindLunchGapBetweenMeetings(meetingsInWindow)
			: FindLunchBreakGap(workEvents);

		return detected ?? (DefaultBreakStart, DefaultBreakEnd);
	}

	/// <summary>
	/// Find a 30-60 minute gap inside the break window between commits/work events
	/// </summary>
	private static (TimeOnly Start, TimeOnly End)? FindLunchBreakGap(List<Event> workEvents)
	{
		var times = workEvents
			.Select(e => TimeOnly.FromDateTime(e.Timestamp))
			.Where(t => t >= BreakWindowStart && t <= BreakWindowEnd)
			.Order()
			.ToList();

		for (var i = 0; i < times.Count - 1; i++)
		{
			if (IsBreakSizedGap(times[i], times[i + 1]))
				return (times[i], times[i].AddMinutes(MinBreakMinutes));
		}

		return null;
	}

	/// <summary>
	/// Find a 30-60 minute gap inside the break window between, before or after meetings
	/// </summary>
	private static (TimeOnly Start, TimeOnly End)? FindLunchGapBetweenMeetings(List<CalendarMeeting> meetingsInWindow)
	{
		var gaps = new List<(TimeOnly Start, TimeOnly End)>();
		for (var i = 0; i < meetingsInWindow.Count - 1; i++)
			gaps.Add((meetingsInWindow[i].End, meetingsInWindow[i + 1].Start));
		gaps.Add((BreakWindowStart, meetingsInWindow[0].Start));
		gaps.Add((meetingsInWindow[^1].End, BreakWindowEnd));

		foreach (var (rawStart, rawEnd) in gaps)
		{
			var start = rawStart < BreakWindowStart ? BreakWindowStart : rawStart;
			var end = rawEnd > BreakWindowEnd ? BreakWindowEnd : rawEnd;
			if (IsBreakSizedGap(start, end))
			{
				Log.Information("Detected lunch break from meeting gap: {Start} - {End}", start, start.AddMinutes(MinBreakMinutes));
				return (start, start.AddMinutes(MinBreakMinutes));
			}
		}

		return null;
	}

	private static bool IsBreakSizedGap(TimeOnly start, TimeOnly end) =>
		end > start && (end - start).TotalMinutes is >= MinBreakMinutes and <= MaxBreakMinutes;

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
    /// Extract ticket number from event metadata
    /// </summary>
    private static string? GetTicketNumber(Event? evt) => evt?.Metadata?.GetValueOrDefault("TicketNumber");

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
	/// Ticket from events: prefer commits (more specific than branch-level tickets), then any event
	/// </summary>
	private static string? GetTicketFromEvents(List<Event> events) =>
		events.Where(e => e.EventType == EventType.Commit).Select(GetTicketNumber).FirstOrDefault(t => t != null)
		?? events.Select(GetTicketNumber).FirstOrDefault(t => t != null);

	/// <summary>
	/// Generate description for time slot based on events: the most detailed commit message, else the first event
	/// </summary>
	private static string GetWorkDescription(List<Event> workEvents)
	{
		var evt = workEvents.Where(e => e.EventType == EventType.Commit).MaxBy(e => e.Description?.Length ?? 0)
			?? workEvents.FirstOrDefault();

		if (evt == null)
			return "Work";

		return string.IsNullOrWhiteSpace(evt.Description) ? evt.EventType.ToString() : evt.Description;
	}

	/// <summary>
	/// Extract metadata (Repository, Branch) from Git events, falling back to the branch active at slot start
	/// </summary>
	private static Dictionary<string, string>? GetMetadataFromEvents(List<Event> workEvents, List<(DateTime Time, string Branch, string? Ticket)> branchHistory, DateTime slotTime)
	{
		var metadata = new Dictionary<string, string>();

		var gitEvent = workEvents.FirstOrDefault(e => e.EventType is EventType.Commit or EventType.BranchSwitch);
		foreach (var key in (string[])["Repository", "Branch"])
		{
			if (gitEvent?.Metadata?.TryGetValue(key, out var value) == true)
				metadata[key] = value;
		}

		if (!metadata.ContainsKey("Branch"))
		{
			// Value tuples: MaxBy throws on an empty sequence instead of returning default
			var earlier = branchHistory.Where(b => b.Time <= slotTime).ToList();
			if (earlier.Count > 0 && earlier.MaxBy(b => b.Time).Branch is { Length: > 0 } branch)
				metadata["Branch"] = branch;
		}

		return metadata.Count > 0 ? metadata : null;
	}
}
