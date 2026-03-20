using System.Text;
using FillMyADT.Models;

namespace FillMyADT.Services;

/// <summary>
/// Formats events and time slots for clipboard export
/// </summary>
public class ClipboardFormatterService
{
    public string FormatEvents(IReadOnlyList<Event> events, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(events);

        var sb = new StringBuilder();
        sb.AppendLine($"=== Events for {date:yyyy-MM-dd} ===");
        sb.AppendLine($"Total: {events.Count} events");
        sb.AppendLine();

        foreach (var evt in events)
        {
            sb.AppendLine($"{evt.Timestamp:HH:mm:ss} - {evt.EventType}");
            sb.AppendLine($"  Source: {evt.Source}");
            
            if (!string.IsNullOrEmpty(evt.Description))
                sb.AppendLine($"  Description: {evt.Description}");
            
            if (evt.Metadata != null && evt.Metadata.Any())
            {
                sb.AppendLine("  Metadata:");
                foreach (var meta in evt.Metadata)
                    sb.AppendLine($"    {meta.Key}: {meta.Value}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string FormatTimeSlots(IReadOnlyList<TimeSlot> timeSlots, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(timeSlots);

        var sb = new StringBuilder();
        sb.AppendLine($"=== Time Slots for {date:yyyy-MM-dd} ===");

        var totalMinutes = timeSlots.Sum(s => s.Duration.TotalMinutes);
        var totalHours = (int)(totalMinutes / 60);
        var totalMins = (int)(totalMinutes % 60);
        sb.AppendLine($"Total: {timeSlots.Count} slots, {totalHours}h {totalMins}m");
        sb.AppendLine();

        foreach (var slot in timeSlots)
        {
            var durationStr = FormatDuration(slot.Duration);

            sb.Append($"{slot.StartTime:HH:mm} - {slot.EndTime:HH:mm} ({durationStr})");
            if (!string.IsNullOrEmpty(slot.TicketNr))
                sb.Append($" [#{slot.TicketNr}]");
            if (!string.IsNullOrEmpty(slot.Source))
                sb.Append($" [{slot.Source}]");
            sb.AppendLine();

            sb.AppendLine($"  {slot.Text}");

            if (!string.IsNullOrEmpty(slot.Location))
                sb.AppendLine($"  📍 {slot.Location}");

            if (slot.Metadata != null && slot.Metadata.Any())
            {
                foreach (var meta in slot.Metadata)
                    sb.AppendLine($"  {meta.Key}: {meta.Value}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var hours = (int)duration.TotalHours;
        var minutes = duration.Minutes;
        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }
}
