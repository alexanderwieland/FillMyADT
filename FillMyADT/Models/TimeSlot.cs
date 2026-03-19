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
    /// Ticket number extracted from the event (if available)
    /// </summary>
    public string? TicketNr { get; init; }

    /// <summary>
    /// Description/text for the time slot
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Location for the time slot (to be added later)
    /// </summary>
    public string? Location { get; init; }

    /// <summary>
    /// Duration of the time slot
    /// </summary>
    public TimeSpan Duration => EndTime - StartTime;
}
