namespace FillMyADT.Models;

/// <summary>
/// Represents a time slot for daily activity tracking
/// </summary>
public record TimeSlot
{
    /// <summary>
    /// Date of the time slot
    /// </summary>
    public required DateOnly Date { get; init; }

    /// <summary>
    /// Start time of the time slot
    /// </summary>
    public required TimeOnly StartTime { get; init; }

    /// <summary>
    /// End time of the time slot
    /// </summary>
    public required TimeOnly EndTime { get; init; }

    /// <summary>
    /// Ticket number(s) extracted from the event (if available) including #
    /// </summary>
    public string? TicketNr
    {
        get;
        init
        {
            if (string.IsNullOrEmpty(value))
            {
                field = value;
                return;
            }

            if (!value.Contains(','))
            {
                field = value.StartsWith('#') ? value : $"#{value}";
                return;
            }

            field = string.Join(", ", value
                .Split(',')
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrEmpty(t))
                .Select(t => t.StartsWith('#') ? t : $"#{t}"));
        }
    }

    /// <summary>
    /// Description/text for the time slot
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Location for the time slot (to be added later)
    /// </summary>
    public Location Location { get; set; } = Location.WV;

    /// <summary>
    /// Additional metadata from the source event
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Source of the time slot (e.g., "Git", "Outlook", "Edge Browser", "Windows")
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Category of the time slot
    /// </summary>
    public TimeSlotCategory Category { get; init; } = TimeSlotCategory.Other;

    /// <summary>
    /// Duration of the time slot
    /// </summary>
    public TimeSpan Duration => EndTime - StartTime;

    /// <summary>
    /// Check if the time slot is valid (positive duration)
    /// </summary>
    public bool IsValid => Duration > TimeSpan.Zero;

    /// <summary>
    /// Format duration as human-readable string (e.g., "2h 30m")
    /// </summary>
    public string FormattedDuration
    {
        get
        {
            var hours = (int)Duration.TotalHours;
            var minutes = Duration.Minutes;
            return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
        }
    }

    /// <summary>
    /// Get CSS class for styling based on category
    /// </summary>
    public string CssClass => Category.GetCssClass();
}

public enum Location
{
    WV,
    HOME,
    ABW
}