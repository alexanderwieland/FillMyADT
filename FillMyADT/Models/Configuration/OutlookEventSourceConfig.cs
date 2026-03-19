namespace FillMyADT.Models.Configuration;

/// <summary>
/// Configuration for Outlook Calendar Event Source (COM Interop)
/// </summary>
public record OutlookEventSourceConfig : EventSourceConfig
{
    public OutlookEventSourceConfig() : base()
    {
        SourceType = "OutlookEventSource";
    }

    /// <summary>
    /// Whether to include Teams meetings
    /// </summary>
    public bool IncludeTeamsMeetings { get; init; } = true;

    /// <summary>
    /// Whether to include all-day events
    /// </summary>
    public bool IncludeAllDayEvents { get; init; } = false;

    /// <summary>
    /// Minimum meeting duration in minutes to include (filter out very short meetings)
    /// </summary>
    public int MinimumDurationMinutes { get; init; } = 5;

    /// <summary>
    /// Only connect to running Outlook instance (don't start new instance)
    /// </summary>
    public bool RequireRunningInstance { get; init; } = false;

    /// <summary>
    /// Subject name for home office whole-day events
    /// </summary>
    public string HomeOfficeEventName { get; init; } = "apw - HO";

    /// <summary>
    /// Subject name for holiday/vacation whole-day events
    /// </summary>
    public string HolidayEventName { get; init; } = "apw - Urlaub";

    /// <summary>
    /// Enable special handling for whole-day home office and holiday events
    /// </summary>
    public bool EnableSpecialWholeDayEvents { get; init; } = true;
}
