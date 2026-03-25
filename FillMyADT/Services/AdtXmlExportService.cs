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

    private readonly AppConfiguration _appConfig;

    public AdtXmlExportService(AppConfiguration appConfig)
    {
        _appConfig = appConfig;
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

        // Save to file
        await File.WriteAllTextAsync(filePath, xml.ToString(), cancellationToken);

        Log.Information("Exported {Count} time slots to ADT XML: {FilePath}", timeSlots.Count, filePath);

        return filePath;
    }

    /// <summary>
    /// Create ADT XML document from time slots
    /// </summary>
    private XDocument CreateAdtXml(IReadOnlyList<TimeSlot> timeSlots, DateTime date)
    {
        var root = new XElement("NewDataSet");

        foreach (var slot in timeSlots)
        {
            if (slot.Category == TimeSlotCategory.Break)
                continue; // Skip breaks

            var adoffen = CreateAdoffenElement(slot, date);
            root.Add(adoffen);
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            root
        );

        return document;
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
            Bereich = GetBereich(slot)
        };
    }

    /// <summary>
    /// Create ADoffen element for a single time slot
    /// </summary>
    private XElement CreateAdoffenElement(TimeSlot slot, DateTime date)
    {
        var entry = CreateExportEntry(slot, date);

        return new XElement("ADoffen",
            new XElement("BA", entry.BA),
            new XElement("Datum", entry.Datum.ToString("yyyy-MM-ddTHH:mm:sszzz")),
            new XElement("Beginn", entry.Beginn),
            new XElement("Ende", entry.Ende),
            new XElement("Dauer", entry.Dauer.ToString("F2").Replace(',', '.')),
            new XElement("DauerWV", entry.Dauer.ToString("F2").Replace(',', '.')),
            new XElement("Ort", entry.Ort),
            new XElement("Betrieb", entry.Betrieb),
            new XElement("Projekt", entry.Projekt),
            new XElement("Dokumentation", entry.Dokumentation),
            new XElement("Bereich", entry.Bereich)
        );
    }

    #region Field Mapping Logic - ADAPT AS NEEDED

    /// <summary>
    /// Get location code (Ort) - ADAPT AS NEEDED
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
    /// Get company/operation (Betrieb) - DEFAULT LOGIC, ADAPT AS NEEDED
    /// </summary>
    private string GetBetrieb(TimeSlot slot)
    {
        // TODO: Add your custom logic here
        // Examples:
        // - Extract from metadata
        // - Map based on project/ticket
        // - Use default value

        if (GetOrt(slot) == Location.ABW || slot.Category == TimeSlotCategory.Startup || slot.Category == TimeSlotCategory.Meeting)
        {
            return "MP2";
        }

        if (slot.Category == TimeSlotCategory.RedmineTickets || slot.Category == TimeSlotCategory.TfsWork || slot.Category == TimeSlotCategory.Work)
        {
            if (string.IsNullOrEmpty(slot.TicketNr))
                return "MIW";
            return "INFG"; // TODO Check Text for Betrieb Name
        }

        return "INFG"; // TODO Load Default Company
    }

    /// <summary>
    /// Get project code (Projekt) - DEFAULT LOGIC, ADAPT AS NEEDED
    /// </summary>
    private string GetProjekt(TimeSlot slot)
    {
        // TODO: Add your custom logic here
        // Examples:
        // - Use ticket number if available
        // - Map category to project codes
        // - Extract from metadata

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

        if (slot.Category == TimeSlotCategory.Meeting)
        {
            return "01-DEV-interneAbstimmungen";
        }

        if (slot.Category == TimeSlotCategory.RedmineTickets)
        {
            return "#23755";
        }

        // Use ticket number if available, otherwise default
        if (!string.IsNullOrEmpty(slot.TicketNr))
        {
            return $"{slot.TicketNr}";
        }
        else         if (slot.Category == TimeSlotCategory.Work)
        {
            return "MIMWART";
        }

            return "02-ORGA-Divers"; // Default project
    }

    /// <summary>
    /// Get documentation text (Dokumentation) - DEFAULT LOGIC, ADAPT AS NEEDED
    /// </summary>
    private string GetDokumentation(TimeSlot slot)
    {
        // TODO: Add your custom logic here
        // Examples:
        // - Shorten/format the text
        // - Use category-specific defaults
        // - Extract from metadata

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

    /// <summary>
    /// Get area/department (Bereich) - DEFAULT LOGIC, ADAPT AS NEEDED
    /// </summary>
    private string GetBereich(TimeSlot slot)
    {
        // TODO: Add your custom logic here
        // Examples:
        // - Map based on project
        // - Extract from metadata
        // - Use category-based logic

        // Map source to department
        return slot.Source switch
        {
            "Git" => "DEV",
            "Outlook" => "DEV",
            "Edge Browser" => "DEV",
            _ => "DEV"
        };
    }

    #endregion
}
