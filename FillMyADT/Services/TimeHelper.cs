namespace FillMyADT.Services;

/// <summary>
/// Shared time rounding and duration formatting helpers.
/// </summary>
public static class TimeHelper
{
    private const int MinutesPerDay = 24 * 60;

    /// <summary>
    /// Round time up to the next multiple of <paramref name="minutes"/>.
    /// </summary>
    public static TimeOnly RoundUp(TimeOnly time, int minutes) => Round(time, minutes, Math.Ceiling);

    /// <summary>
    /// Round time down to the previous multiple of <paramref name="minutes"/>.
    /// </summary>
    public static TimeOnly RoundDown(TimeOnly time, int minutes) => Round(time, minutes, Math.Floor);

    /// <summary>
    /// Round time to the nearest multiple of <paramref name="minutes"/>.
    /// </summary>
    public static TimeOnly RoundNearest(TimeOnly time, int minutes) => Round(time, minutes, Math.Round);

    /// <summary>
    /// Format a duration as e.g. "2h 30m", "2h" or "45m".
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var totalMinutes = (int)duration.TotalMinutes;
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        if (hours == 0)
            return $"{minutes}m";

        return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
    }

    private static TimeOnly Round(TimeOnly time, int minutes, Func<double, double> rounding)
    {
        var totalMinutes = time.Hour * 60 + time.Minute;
        var rounded = (int)rounding(totalMinutes / (double)minutes) * minutes;

        // Never roll over past midnight
        if (rounded >= MinutesPerDay)
            rounded = MinutesPerDay - minutes;

        return new TimeOnly(rounded / 60, rounded % 60);
    }
}
