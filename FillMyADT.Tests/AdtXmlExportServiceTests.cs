using FillMyADT.Models;
using FillMyADT.Services;

namespace FillMyADT.Tests;

public class AdtXmlExportServiceTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private static TimeSlot Slot(string start, string end, TimeSlotCategory category, string text = "Work", string? ticket = null, Location location = Location.WV) => new()
    {
        Date = Monday,
        StartTime = TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
        EndTime = TimeOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture),
        Category = category,
        Text = text,
        TicketNr = ticket,
        Location = location
    };

    private static AdtXmlExportService CreateService() => new(AdtPipeline.CreateConfiguration());

    [Fact]
    public void WhenSlotIsBreakThenItIsNotExported()
    {
        var entries = CreateService().GetExportPreview(
            [Slot("12:00", "12:30", TimeSlotCategory.Break, "Lunch Break")], Monday.ToDateTime(TimeOnly.MinValue));

        Assert.Empty(entries);
    }

    [Theory]
    [InlineData(TimeSlotCategory.Work, "12345", "INFG #12345 | Umsetzung")]
    [InlineData(TimeSlotCategory.Work, null, "MIW MIMWART | Work")]
    [InlineData(TimeSlotCategory.Work, "12345, 67890", "INFG MIMWART | Umsetzung")]
    [InlineData(TimeSlotCategory.TfsWork, "42881, 42882", "INFG MIMWART | Review")]
    [InlineData(TimeSlotCategory.TfsWork, "42881", "INFG #42881 | Review")]
    [InlineData(TimeSlotCategory.RedmineTickets, "23001", "INFG #23755 | Ticketmanagement #23001")]
    [InlineData(TimeSlotCategory.Meeting, null, "MP2 01-DEV-interneAbstimmungen | Work")]
    [InlineData(TimeSlotCategory.Startup, null, "MP2 02-ORGA-Divers | ")]
    [InlineData(TimeSlotCategory.Urlaub, null, "MP2 04-ABW-Urlaub | Urlaub")]
    [InlineData(TimeSlotCategory.Zeitausgleich, null, "MP2 04-ABW-Zeitausgleich | ZA")]
    public void WhenCategoryIsMappedThenBetriebProjektAndDokumentationMatch(TimeSlotCategory category, string? ticket, string expected)
    {
        var entry = CreateService().GetExportPreview(
            [Slot("08:00", "09:00", category, ticket: ticket)], Monday.ToDateTime(TimeOnly.MinValue)).Single();

        Assert.Equal(expected, $"{entry.Betrieb} {entry.Projekt} | {entry.Dokumentation}");
    }

    [Theory]
    [InlineData(TimeSlotCategory.Urlaub, Location.WV, Location.ABW)]
    [InlineData(TimeSlotCategory.Zeitausgleich, Location.HOME, Location.ABW)]
    [InlineData(TimeSlotCategory.Work, Location.HOME, Location.HOME)]
    [InlineData(TimeSlotCategory.Meeting, Location.WV, Location.WV)]
    public void WhenSlotHasLocationThenOrtIsMapped(TimeSlotCategory category, Location location, Location expected)
    {
        var entry = CreateService().GetExportPreview(
            [Slot("08:00", "09:00", category, location: location)], Monday.ToDateTime(TimeOnly.MinValue)).Single();

        Assert.Equal(expected, entry.Ort);
    }

    [Fact]
    public void WhenExportedToXmlThenRowContainsAllAdtFields()
    {
        var xml = CreateService().CreateAdtXml(
            [Slot("08:00", "09:45", TimeSlotCategory.Work, ticket: "12345")], Monday.ToDateTime(TimeOnly.MinValue));

        var row = xml.Root!.Element("ADoffen")!;
        var fields = row.Elements().Where(e => e.Name != "Datum").Select(e => $"{e.Name}={e.Value}");

        Assert.Equal(
        [
            "BA=apw", "Beginn=08:00", "Ende=09:45", "Dauer=1.75", "DauerWV=1.75", "Ort=WV",
            "Betrieb=INFG", "Projekt=#12345", "Dokumentation=Umsetzung", "Bereich=DEV",
        ], fields);
    }

    [Fact]
    public void WhenExportedToXmlThenDatumIsTheExportDate()
    {
        var xml = CreateService().CreateAdtXml(
            [Slot("08:00", "09:00", TimeSlotCategory.Work)], Monday.ToDateTime(TimeOnly.MinValue));

        var datum = DateTimeOffset.Parse(xml.Root!.Element("ADoffen")!.Element("Datum")!.Value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(new DateTime(2026, 10, 5), datum.DateTime);
    }
}
