using FillMyADT.Models;
using FillMyADT.Models.Configuration;
using FillMyADT.Services.EventSources;
using Transition = FillMyADT.Services.EventSources.WindowsEventSource.Transition;

namespace FillMyADT.Tests;

/// <summary>
/// End-to-end scenarios: input events of a day -> expected ADT export rows
/// </summary>
public class DayScenarioTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Fact]
    public async Task WhenRegularOfficeDayThenMeetingsLunchAndTicketWorkAreExported()
    {
        var day = new DayBuilder(Monday)
            .Boot("07:47")
            .Lunch("12:00", "12:30")
            .Meeting("10:30", "11:00", "Daily")
            .Commit("09:00", "12345 Fix login", ticket: "12345")
            .Shutdown("16:32");

        var lines = await AdtPipeline.ExportLinesAsync(day);

        Assert.Equal(
        [
            "07:50-08:00 WV MP2 02-ORGA-Divers | ",
            "08:00-10:30 WV INFG #12345 | Umsetzung",
            "10:30-11:00 WV MP2 01-DEV-interneAbstimmungen | Daily",
            "11:00-12:00 WV INFG #12345 | Umsetzung",
            "12:30-16:30 WV INFG #12345 | Umsetzung",
        ], lines);
    }

    [Fact]
    public async Task WhenWorkHasNoTicketThenMaintenanceProjectIsExported()
    {
        var day = new DayBuilder(Monday)
            .Boot("08:00")
            .Lunch("12:00", "12:30")
            .Shutdown("13:00");

        var lines = await AdtPipeline.ExportLinesAsync(day);

        Assert.Equal(
        [
            "08:00-08:15 WV MP2 02-ORGA-Divers | ",
            "08:15-12:00 WV MIW MIMWART | Work",
            "12:30-13:00 WV MIW MIMWART | Work",
        ], lines);
    }

    [Fact]
    public async Task WhenHomeofficeDayThenAllRowsAreHome()
    {
        var day = new DayBuilder(Monday)
            .HomeofficeDay()
            .Boot("07:47")
            .Lunch("12:00", "12:30")
            .Shutdown("16:32");

        var locations = (await AdtPipeline.ExportAsync(day)).Select(e => e.Ort).Distinct();

        Assert.Equal([Location.HOME], locations);
    }

    [Fact]
    public async Task WhenHolidayThenSingleAbsenceRowWithConfiguredWorkHours()
    {
        var day = new DayBuilder(Monday).HolidayDay().Boot("07:47").Shutdown("16:32");

        var lines = await AdtPipeline.ExportLinesAsync(day);

        Assert.Equal(["08:00-14:24 ABW MP2 04-ABW-Urlaub | Urlaub"], lines);
    }

    [Fact]
    public async Task WhenZeitausgleichThenSingleZeroDurationAbsenceRow()
    {
        var day = new DayBuilder(Monday).ZeitausgleichDay();

        var lines = await AdtPipeline.ExportLinesAsync(day);

        Assert.Equal(["00:00-00:00 ABW MP2 04-ABW-Zeitausgleich | ZA"], lines);
    }

    [Fact]
    public async Task WhenNoBootEventThenNothingIsExported()
    {
        var day = new DayBuilder(Monday).Meeting("10:00", "11:00", "Daily");

        var entries = await AdtPipeline.ExportAsync(day);

        Assert.Empty(entries);
    }

    /// <summary>
    /// Regression: the machine slept 09:53-14:21 while in meetings. This must not be treated as
    /// "left the office" - meetings and lunch stay and nothing is marked as HOME.
    /// </summary>
    [Fact]
    public async Task WhenMachineSleepsDuringTheDayThenMeetingsAreKeptAndLocationStaysOffice()
    {
        var day = new DayBuilder(Monday)
            .With(Windows(Monday, ("07:47", Transition.Wake), ("09:53", Transition.Sleep), ("14:21", Transition.Wake), ("17:34", Transition.Sleep)))
            .Meeting("10:30", "11:00", "DEV-Sprint")
            .Meeting("13:00", "13:30", "Status-Meeting");

        var lines = await AdtPipeline.ExportLinesAsync(day);

        Assert.Equal(
        [
            "07:50-08:00 WV MP2 02-ORGA-Divers | ",
            "08:00-10:30 WV MIW MIMWART | Work",
            "10:30-11:00 WV MP2 01-DEV-interneAbstimmungen | DEV-Sprint",
            "11:00-12:00 WV MIW MIMWART | Work",
            "12:30-13:00 WV MIW MIMWART | Work",
            "13:00-13:30 WV MP2 01-DEV-interneAbstimmungen | Status-Meeting",
            "13:30-17:30 WV MIW MIMWART | Work",
        ], lines);
    }

    [Fact]
    public async Task WhenShutDownInTheEveningAndRestartedLaterThenOffTimeIsCutAndLaterWorkIsHome()
    {
        var day = new DayBuilder(Monday)
            .With(Windows(Monday, ("07:47", Transition.Wake), ("17:00", Transition.Sleep), ("20:00", Transition.Wake), ("21:00", Transition.Sleep)));

        var lines = await AdtPipeline.ExportLinesAsync(day);

        Assert.Equal(
        [
            "07:50-08:00 WV MP2 02-ORGA-Divers | ",
            "08:00-12:00 WV MIW MIMWART | Work",
            "12:30-17:00 WV MIW MIMWART | Work",
            "20:00-21:00 HOME MIW MIMWART | Work",
        ], lines);
    }

    private static IReadOnlyList<Event> Windows(DateOnly date, params (string Time, Transition Kind)[] transitions)
    {
        var source = new WindowsEventSource(new WindowsEventSourceConfig());
        return source.CreateEventsFromTransitions(transitions.Select(t =>
            (date.ToDateTime(TimeOnly.Parse(t.Time, System.Globalization.CultureInfo.InvariantCulture)), t.Kind)));
    }
}
