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
    /// Get display name for category
    /// </summary>
    public static string GetDisplayName(this TimeSlotCategory category) => category switch
    {
        TimeSlotCategory.Startup => "Startup",
        TimeSlotCategory.Work => "Work",
        TimeSlotCategory.Meeting => "Meeting",
        TimeSlotCategory.Break => "Break",
        TimeSlotCategory.RedmineTickets => "Redmine Tickets",
        TimeSlotCategory.TfsWork => "TFS Work",
        TimeSlotCategory.Homeoffice => "Home Office",
        TimeSlotCategory.Urlaub => "Holiday",
        TimeSlotCategory.Other => "Other",
        _ => category.ToString()
    };

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
        TimeSlotCategory.Other => "category-default",
        _ => "category-default"
    };

    public static TimeSlotCategory ParseEventType(EventType eventType) =>
     eventType switch
     {
         EventType.Boot => TimeSlotCategory.Startup,
         EventType.Shutdown => TimeSlotCategory.Work, // oder Other? Je nach Logik

         EventType.CalendarMeetingStart => TimeSlotCategory.Meeting,
         EventType.CalendarMeetingEnd => TimeSlotCategory.Meeting,

         EventType.TicketStart => TimeSlotCategory.RedmineTickets,
         EventType.TicketEnd => TimeSlotCategory.RedmineTickets,

         EventType.LunchBreakStart => TimeSlotCategory.Break,
         EventType.LunchBreakEnd => TimeSlotCategory.Break,

         EventType.SpecialWholeDayHoliday => TimeSlotCategory.Urlaub,
         EventType.SpecialWholeDayZeitausgleich => TimeSlotCategory.Zeitausgleich,
         EventType.SpecialWholeDayHomeoffice => TimeSlotCategory.Homeoffice,

         EventType.ReviewStart => TimeSlotCategory.TfsWork,
         EventType.ReviewEnd => TimeSlotCategory.TfsWork,

         EventType.BranchSwitch => TimeSlotCategory.Work,
         EventType.Commit => TimeSlotCategory.Work,

         EventType.None => TimeSlotCategory.Other,

         _ => TimeSlotCategory.Other
     };
}
