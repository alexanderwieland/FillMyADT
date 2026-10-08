namespace FillMyADT.Models;

/// <summary>
/// Categories for time slot classification
/// </summary>
public enum TimeSlotCategory
{
    /// <summary>
    /// System startup/boot period
    /// </summary>
    Startup,

    /// <summary>
    /// Active work period, aka Git
    /// </summary>
    Work,

    /// <summary>
    /// Meeting or calendar event (Outlook)
    /// </summary>
    Meeting,

    /// <summary>
    /// Break period (lunch, coffee, etc.)
    /// </summary>
    Break,

    /// <summary>
    /// Redmine ticket management
    /// </summary>
    RedmineTickets,

    /// <summary>
    /// TFS work item or pull request
    /// </summary>
    TfsWork,

    /// <summary>
    /// Home office day
    /// </summary>
    Homeoffice,

    /// <summary>
    /// Holiday or vacation day
    /// </summary>
    Urlaub,

    /// <summary>
    /// Time compensation day (Zeitausgleich)
    /// </summary>
    Zeitausgleich,

    /// <summary>
    /// Uncategorized or unknown
    /// </summary>
    Other
}

/// <summary>
/// Extension methods for TimeSlotCategory
/// </summary>
public static class TimeSlotCategoryExtensions
{
    /// <summary>
    /// Get CSS class name for styling
    /// </summary>
    public static string GetCssClass(this TimeSlotCategory category) => category switch
    {
        TimeSlotCategory.Startup => "category-startup",
        TimeSlotCategory.Work => "category-work",
        TimeSlotCategory.Meeting => "category-meeting",
        TimeSlotCategory.Break => "category-break",
        TimeSlotCategory.RedmineTickets => "category-redmine",
        TimeSlotCategory.TfsWork => "category-tfs",
        TimeSlotCategory.Homeoffice => "category-homeoffice",
        TimeSlotCategory.Urlaub => "category-holiday",
        _ => "category-default"
    };

    public static TimeSlotCategory ParseEventType(EventType eventType) => eventType switch
    {
        EventType.Boot => TimeSlotCategory.Startup,
        EventType.Shutdown or EventType.BranchSwitch or EventType.Commit => TimeSlotCategory.Work,
        EventType.CalendarMeetingStart or EventType.CalendarMeetingEnd => TimeSlotCategory.Meeting,
        EventType.TicketStart or EventType.TicketEnd => TimeSlotCategory.RedmineTickets,
        EventType.LunchBreakStart or EventType.LunchBreakEnd => TimeSlotCategory.Break,
        EventType.SpecialWholeDayHoliday => TimeSlotCategory.Urlaub,
        EventType.SpecialWholeDayZeitausgleich => TimeSlotCategory.Zeitausgleich,
        EventType.SpecialWholeDayHomeoffice => TimeSlotCategory.Homeoffice,
        EventType.ReviewStart or EventType.ReviewEnd => TimeSlotCategory.TfsWork,
        _ => TimeSlotCategory.Other
    };
}
