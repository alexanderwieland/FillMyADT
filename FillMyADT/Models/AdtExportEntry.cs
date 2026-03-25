namespace FillMyADT.Models;

/// <summary>
/// Represents a single ADT export entry with all mapped fields
/// </summary>
public record AdtExportEntry
{
    /// <summary>
    /// Original time slot being exported
    /// </summary>
    public required TimeSlot TimeSlot { get; init; }

    /// <summary>
    /// BA - Bearbeiter (initials)
    /// </summary>
    public required string BA { get; init; }

    /// <summary>
    /// Datum - Date
    /// </summary>
    public required DateTime Datum { get; init; }

    /// <summary>
    /// Beginn - Start time (HH:mm)
    /// </summary>
    public required string Beginn { get; init; }

    /// <summary>
    /// Ende - End time (HH:mm)
    /// </summary>
    public required string Ende { get; init; }

    /// <summary>
    /// Dauer - Duration in hours (decimal)
    /// </summary>
    public required double Dauer { get; init; }

    /// <summary>
    /// Ort - Location code
    /// </summary>
    public required Location Ort { get; init; }

    /// <summary>
    /// Betrieb - Company/Operation
    /// </summary>
    public required string Betrieb { get; init; }

    /// <summary>
    /// Projekt - Project code
    /// </summary>
    public required string Projekt { get; init; }

    /// <summary>
    /// Dokumentation - Documentation text
    /// </summary>
    public required string Dokumentation { get; init; }

    /// <summary>
    /// Bereich - Area/Department
    /// </summary>
    public required string Bereich { get; init; }

    /// <summary>
    /// Formatted duration for display (e.g., "2.50h")
    /// </summary>
    public string FormattedDauer => $"{Dauer:F2}h";
}
