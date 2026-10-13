using FillMyADT.Models;
using FillMyADT.Services;

namespace FillMyADT.Tests;

/// <summary>
/// Runs the real conversion pipeline (events -> time slots -> ADT export entries) with an in-memory configuration
/// </summary>
public static class AdtPipeline
{
    public static ConfigurationService CreateConfiguration() =>
        new(new AppConfiguration { Initials = "apw", WorkHours = 6.4, DefaultLocation = Location.WV });

    public static async Task<IReadOnlyList<AdtExportEntry>> ExportAsync(DayBuilder day)
    {
        var configuration = CreateConfiguration();
        var converter = new EventToTimeSpanConverter(configurationService: configuration);
        var slots = await converter.ConvertEventsToTimeSlotsAsync(day.Build(), TestContext.Current.CancellationToken);
        return new AdtXmlExportService(configuration).GetExportPreview(slots, day.Date.ToDateTime(TimeOnly.MinValue));
    }

    /// <summary>
    /// Compact, diff-friendly representation of the exported rows: "Beginn-Ende Ort Betrieb Projekt | Dokumentation"
    /// </summary>
    public static async Task<IReadOnlyList<string>> ExportLinesAsync(DayBuilder day) =>
        (await ExportAsync(day)).Select(Format).ToList();

    public static string Format(AdtExportEntry e) => $"{e.Beginn}-{e.Ende} {e.Ort} {e.Betrieb} {e.Projekt} | {e.Dokumentation}";
}
