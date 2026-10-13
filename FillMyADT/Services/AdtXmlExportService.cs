using FillMyADT.Models;
using Serilog;
using System.IO;
using System.Xml.Linq;

namespace FillMyADT.Services;

/// <summary>
/// Service for exporting time slots to ADT XML format
/// </summary>
public class AdtXmlExportService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<AdtXmlExportService>();

    private readonly ConfigurationService _configurationService;

    private AppConfiguration _appConfig => _configurationService.Current;

    public AdtXmlExportService(ConfigurationService configurationService)
    {
        ArgumentNullException.ThrowIfNull(configurationService);
        _configurationService = configurationService;
    }

    /// <summary>
    /// Get export preview without writing to file
    /// </summary>
    public IReadOnlyList<AdtExportEntry> GetExportPreview(IReadOnlyList<TimeSlot> timeSlots, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(timeSlots);

        var exportEntries = new List<AdtExportEntry>();

        foreach (var slot in timeSlots)
        {
            if (slot.Category == TimeSlotCategory.Break)
                continue; // Skip breaks

            var entry = CreateExportEntry(slot, date);
            exportEntries.Add(entry);
        }

        return exportEntries;
    }

    /// <summary>
    /// Export time slots to ADT XML format
    /// </summary>
    public async Task<string> ExportToXmlAsync(IReadOnlyList<TimeSlot> timeSlots, DateTime date, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeSlots);

        var exportDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ADT08",
            "data"
        );

        // Ensure export directory exists
        if (!Directory.Exists(exportDirectory))
        {
            Directory.CreateDirectory(exportDirectory);
            Log.Information("Created ADT export directory: {Directory}", exportDirectory);
        }

        // Generate filename: doku_apw_27_03_2026.xml
        var filename = $"doku_{_appConfig.Initials}_{date:dd_MM_yyyy}.xml";
        var filePath = Path.Combine(exportDirectory, filename);

        // Create XML document
        var xml = CreateAdtXml(timeSlots, date);

        // Save to file (XDocument.ToString() would drop the XML declaration)
        await using (var stream = File.Create(filePath))
        {
            await xml.SaveAsync(stream, SaveOptions.None, cancellationToken);
        }

        Log.Information("Exported {Count} entries to ADT XML: {FilePath}", xml.Root!.Elements().Count(), filePath);

        return filePath;
    }

    /// <summary>
    /// Create ADT XML document from time slots (breaks are skipped)
    /// </summary>
    public XDocument CreateAdtXml(IReadOnlyList<TimeSlot> timeSlots, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(timeSlots);

        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("NewDataSet", GetExportPreview(timeSlots, date).Select(CreateAdoffenElement))
        );
    }

    /// <summary>
    /// Create export entry from time slot
    /// </summary>
    private AdtExportEntry CreateExportEntry(TimeSlot slot, DateTime date)
    {
        var durationHours = slot.Duration.TotalHours;

        return new AdtExportEntry
        {
            TimeSlot = slot,
            BA = _appConfig.Initials,
            Datum = date,
            Beginn = slot.StartTime.ToString("HH:mm"),
            Ende = slot.EndTime.ToString("HH:mm"),
            Dauer = durationHours,
            Ort = GetOrt(slot),
            Betrieb = GetBetrieb(slot),
            Projekt = GetProjekt(slot),
            Dokumentation = GetDokumentation(slot),
            Bereich = "DEV"
        };
    }

    /// <summary>
    /// Create ADoffen element for a single export entry
    /// </summary>
    private static XElement CreateAdoffenElement(AdtExportEntry entry)
    {
        var duration = entry.Dauer.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        return new XElement("ADoffen",
            new XElement("BA", entry.BA),
            new XElement("Datum", entry.Datum.ToString("yyyy-MM-ddTHH:mm:sszzz")),
            new XElement("Beginn", entry.Beginn),
            new XElement("Ende", entry.Ende),
            new XElement("Dauer", duration),
            new XElement("DauerWV", duration),
            new XElement("Ort", entry.Ort),
            new XElement("Betrieb", entry.Betrieb),
            new XElement("Projekt", entry.Projekt),
            new XElement("Dokumentation", entry.Dokumentation),
            new XElement("Bereich", entry.Bereich)
        );
    }

    #region Field Mapping Logic

    /// <summary>
    /// Get location code (Ort)
    /// </summary>
    private Location GetOrt(TimeSlot slot)
    {
        // Special handling for absence categories
        if (slot.Category == TimeSlotCategory.Urlaub ||
            slot.Category == TimeSlotCategory.Zeitausgleich)
        {
            return Location.ABW; // Abwesenheit (absence)
        }

        // Use location from slot, or default
        return slot.Location;
    }

    /// <summary>
    /// Get company/operation (Betrieb)
    /// </summary>
    private string GetBetrieb(TimeSlot slot)
    {
        if (GetOrt(slot) == Location.ABW || slot.Category == TimeSlotCategory.Startup)
        {
            return "MP2";
        }

        if (slot.Category == TimeSlotCategory.RedmineTickets || slot.Category == TimeSlotCategory.TfsWork || slot.Category == TimeSlotCategory.Work)
        {
            if (string.IsNullOrEmpty(slot.TicketNr))
                return "MIW";
            return "INFG"; // TODO Check Text for Betrieb Name
        }

        if (slot.Category == TimeSlotCategory.Meeting && slot.Text.ToLowerInvariant().Contains("nuhr"))
        {
            return "NUHR";
        }
        else if (slot.Category == TimeSlotCategory.Meeting && slot.Text.ToLowerInvariant().Contains("bd2"))
        {
            return "BD2";
        }
        else if (slot.Category == TimeSlotCategory.Meeting)
        {
            return "MP2";
        }

        return "INFG";
    }

    /// <summary>
    /// Get project code (Projekt)
    /// </summary>
    private string GetProjekt(TimeSlot slot)
    {
        // Special handling for absence categories
        if (slot.Category == TimeSlotCategory.Urlaub)
        {
            return "04-ABW-Urlaub";
        }

        if (slot.Category == TimeSlotCategory.Zeitausgleich)
        {
            return "04-ABW-Zeitausgleich";
        }

        if (slot.Category == TimeSlotCategory.Startup)
        {
            return "02-ORGA-Divers";
        }

        if (slot.Category == TimeSlotCategory.Meeting && slot.Text.ToLowerInvariant().Contains("nuhr"))
        {
            return "#37004";
        }
        else if (slot.Category == TimeSlotCategory.Meeting && slot.Text.ToLowerInvariant().Contains("bd2"))
        {
            return "#41701";
        }
        else if (slot.Category == TimeSlotCategory.Meeting)
        {
            return "01-DEV-interneAbstimmungen";
        }

        if (slot.Category == TimeSlotCategory.RedmineTickets)
        {
            return "#23755";
        }

        // Use ticket number if available; several tickets can't be booked on one project
        if (!string.IsNullOrEmpty(slot.TicketNr) && !slot.TicketNr.Contains(','))
        {
            return slot.TicketNr;
        }
        if (slot.Category == TimeSlotCategory.Work || !string.IsNullOrEmpty(slot.TicketNr))
        {
            return "MIMWART";
        }

        return "02-ORGA-Divers"; // Default project
    }

    /// <summary>
    /// Get documentation text (Dokumentation)
    /// </summary>
    private string GetDokumentation(TimeSlot slot)
    {
        // Special handling for absence categories
        if (slot.Category == TimeSlotCategory.Urlaub)
        {
            return "Urlaub";
        }

        if (slot.Category == TimeSlotCategory.Zeitausgleich)
        {
            return "ZA";
        }

        if (slot.Category == TimeSlotCategory.Startup)
        {
            return "";
        }

        if (!string.IsNullOrEmpty(slot.TicketNr) && slot.Category == TimeSlotCategory.Work)
        {
            return "Umsetzung";
        }

        if (!string.IsNullOrEmpty(slot.TicketNr) && slot.Category == TimeSlotCategory.TfsWork)
        {
            return "Review";
        }

        if (slot.Category == TimeSlotCategory.RedmineTickets)
        {
            return !string.IsNullOrEmpty(slot.TicketNr)
                ? $"Ticketmanagement {slot.TicketNr}"
                : "Ticketmanagement";
        }

        // Use slot text, truncate if too long
        var text = slot.Text ?? "Umsetzung";
        return text.Length > 100 ? text.Substring(0, 100) : text;
    }

    #endregion
}
