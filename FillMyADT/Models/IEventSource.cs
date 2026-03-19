namespace FillMyADT.Models;

/// <summary>
/// Interface for event sources that can provide events for a specific date range
/// </summary>
public interface IEventSource
{
    /// <summary>
    /// Name of the event source
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Retrieve events for the specified date range
    /// </summary>
    /// <param name="startDate">Start date (inclusive)</param>
    /// <param name="endDate">End date (inclusive)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of events</returns>
    Task<IEnumerable<Event>> GetEventsAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if this event source is available/configured
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
