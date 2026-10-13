using FillMyADT.Models;

namespace FillMyADT.Tests;

/// <summary>
/// Fluent builder for the aggregated input events of a single day
/// </summary>
public sealed class DayBuilder(DateOnly date)
{
    private readonly List<Event> _events = [];

    public DateOnly Date { get; } = date;

    public DayBuilder Boot(string time, bool resume = false) => Add(time, EventType.Boot, "Windows Events",
        metadata: resume ? new() { ["Resume"] = "true" } : null);

    public DayBuilder Shutdown(string time) => Add(time, EventType.Shutdown, "Windows Events");

    public DayBuilder Lunch(string start, string end) =>
        Add(start, EventType.LunchBreakStart, "Windows Events").Add(end, EventType.LunchBreakEnd, "Windows Events");

    public DayBuilder Meeting(string start, string end, string subject) =>
        Add(start, EventType.CalendarMeetingStart, "Outlook Calendar", subject)
            .Add(end, EventType.CalendarMeetingEnd, "Outlook Calendar", subject);

    public DayBuilder Commit(string time, string message, string? ticket = null) =>
        Add(time, EventType.Commit, "Git History", message,
            ticket == null ? null : new() { ["TicketNumber"] = ticket });

    public DayBuilder HomeofficeDay() => Add("00:00", EventType.SpecialWholeDayHomeoffice, "Outlook Calendar", "apw - Homeoffice");

    public DayBuilder HolidayDay() => Add("00:00", EventType.SpecialWholeDayHoliday, "Outlook Calendar", "Urlaub");

    public DayBuilder ZeitausgleichDay() => Add("00:00", EventType.SpecialWholeDayZeitausgleich, "Outlook Calendar", "ZA");

    public DayBuilder With(IEnumerable<Event> events)
    {
        _events.AddRange(events);
        return this;
    }

    public IReadOnlyList<Event> Build() => _events.ToList();

    private DayBuilder Add(string time, EventType type, string source, string? description = null, Dictionary<string, string>? metadata = null)
    {
        _events.Add(new Event
        {
            Source = source,
            Timestamp = At(time),
            EventType = type,
            Description = description,
            Metadata = metadata
        });
        return this;
    }

    public DateTime At(string time) => Date.ToDateTime(TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture));
}
