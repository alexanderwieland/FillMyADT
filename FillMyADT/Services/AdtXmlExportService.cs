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
    /// Create ADoffen element for a single time slot
    /// </summary>
    private XElement CreateAdoffenElement(TimeSlot slot, DateTime date)
    {
        var durationHours = slot.Duration.TotalHours;

        // Get location (use ABW for special categories)
        var ort = GetOrt(slot);

        // Get Betrieb (company/operation) - DEFAULT LOGIC, ADAPT AS NEEDED
        var betrieb = GetBetrieb(slot);

        // Get Projekt (project) - DEFAULT LOGIC, ADAPT AS NEEDED
        var projekt = GetProjekt(slot);

        // Get Dokumentation (documentation) - DEFAULT LOGIC, ADAPT AS NEEDED
        var dokumentation = GetDokumentation(slot);

        // Get Bereich (area/department) - DEFAULT LOGIC, ADAPT AS NEEDED
        var bereich = GetBereich(slot);

        return new XElement("ADoffen",
            new XElement("BA", _appConfig.Initials),
            new XElement("Datum", date.ToString("yyyy-MM-ddTHH:mm:sszzz")),
            new XElement("Beginn", slot.StartTime.ToString("HH:mm")),
            new XElement("Ende", slot.EndTime.ToString("HH:mm")),
            new XElement("Dauer", durationHours.ToString("F2").Replace(',', '.')),
            new XElement("DauerWV", durationHours.ToString("F2").Replace(',', '.')),
            new XElement("Ort", ort),
            new XElement("Betrieb", betrieb),
            new XElement("Projekt", projekt),
            new XElement("Dokumentation", dokumentation),
            new XElement("Bereich", bereich)
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
            return "Ticketmanagement";
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
