namespace FillMyADT.Models.Configuration;

/// <summary>
/// Configuration for Outlook Calendar Event Source (COM Interop)
/// </summary>
public record OutlookEventSourceConfig : EventSourceConfig
{
    /// <summary>
    /// Whether to include Teams meetings
    /// </summary>
    public bool IncludeTeamsMeetings { get; set; } = true;

    /// <summary>
    /// Whether to include all-day events
    /// </summary>
    public bool IncludeAllDayEvents { get; set; } = false;

    /// <summary>
    /// Minimum meeting duration in minutes to include (filter out very short meetings)
    /// </summary>
    public int MinimumDurationMinutes { get; set; } = 5;

    /// <summary>
    /// Only connect to running Outlook instance (don't start new instance)
    /// </summary>
    public bool RequireRunningInstance { get; set; } = false;

    /// <summary>
    /// Suffix for home office whole-day events (will be combined with user initials)
    /// </summary>
    public string HomeOfficeSuffix { get; set; } = "HO";

    /// <summary>
    /// Suffix for holiday/vacation whole-day events (will be combined with user initials)
    /// </summary>
    public string HolidaySuffix { get; set; } = "Urlaub";

    /// <summary>
    /// Suffix for time compensation (Zeitausgleich) whole-day events (will be combined with user initials)
    /// </summary>
    public string ZeitausgleichSuffix { get; set; } = "ZA";

    /// <summary>
    /// Enable special handling for whole-day home office and holiday events
    /// </summary>
    public bool EnableSpecialWholeDayEvents { get; set; } = true;
}
