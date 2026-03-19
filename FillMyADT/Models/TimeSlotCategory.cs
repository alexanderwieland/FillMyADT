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
    /// Active work period
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
    Holiday,

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
        TimeSlotCategory.Holiday => "Holiday",
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
        TimeSlotCategory.Holiday => "category-holiday",
        TimeSlotCategory.Other => "category-default",
        _ => "category-default"
    };

    /// <summary>
    /// Parse string to category (backwards compatibility)
    /// </summary>
    public static TimeSlotCategory ParseCategory(string? category) => category?.ToLowerInvariant() switch
    {
        "startup" => TimeSlotCategory.Startup,
        "work" => TimeSlotCategory.Work,
        "meeting" => TimeSlotCategory.Meeting,
        "break" => TimeSlotCategory.Break,
        "redminetickets" => TimeSlotCategory.RedmineTickets,
        "tfswork" => TimeSlotCategory.TfsWork,
        "homeoffice" => TimeSlotCategory.Homeoffice,
        "holiday" => TimeSlotCategory.Holiday,
        null => TimeSlotCategory.Other,
        _ => TimeSlotCategory.Other
    };
}
