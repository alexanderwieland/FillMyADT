namespace FillMyADT.Models;

/// <summary>
/// Strongly-typed metadata for time slots
/// </summary>
public record TimeSlotMetadata
{
    /// <summary>
    /// Git branch name
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// Git repository name
    /// </summary>
    public string? Repository { get; init; }

    /// <summary>
    /// Original timestamp before rounding
    /// </summary>
    public DateTime? OriginalTime { get; init; }

    /// <summary>
    /// Detection method used
    /// </summary>
    public string? DetectionMethod { get; init; }

    /// <summary>
    /// Source event ID
    /// </summary>
    public string? EventId { get; init; }

    /// <summary>
    /// Source of the event
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Create from dictionary (backwards compatibility)
    /// </summary>
    public static TimeSlotMetadata FromDictionary(Dictionary<string, string>? metadata)
    {
        if (metadata == null)
            return new TimeSlotMetadata();

        return new TimeSlotMetadata
        {
            Branch = metadata.GetValueOrDefault("Branch"),
            Repository = metadata.GetValueOrDefault("Repository"),
            OriginalTime = metadata.TryGetValue("OriginalTime", out var time) && DateTime.TryParse(time, out var dt) ? dt : null,
            DetectionMethod = metadata.GetValueOrDefault("DetectionMethod"),
            EventId = metadata.GetValueOrDefault("EventId"),
            Source = metadata.GetValueOrDefault("Source")
        };
    }

    /// <summary>
    /// Convert to dictionary (for serialization/backwards compatibility)
    /// </summary>
    public Dictionary<string, string> ToDictionary()
    {
        var dict = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(Branch))
            dict["Branch"] = Branch;
        if (!string.IsNullOrEmpty(Repository))
            dict["Repository"] = Repository;
        if (OriginalTime.HasValue)
            dict["OriginalTime"] = OriginalTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (!string.IsNullOrEmpty(DetectionMethod))
            dict["DetectionMethod"] = DetectionMethod;
        if (!string.IsNullOrEmpty(EventId))
            dict["EventId"] = EventId;
        if (!string.IsNullOrEmpty(Source))
            dict["Source"] = Source;

        return dict;
    }
}
