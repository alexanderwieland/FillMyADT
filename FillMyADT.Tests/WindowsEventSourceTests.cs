using FillMyADT.Models;
using FillMyADT.Models.Configuration;
using FillMyADT.Services.EventSources;
using Transition = FillMyADT.Services.EventSources.WindowsEventSource.Transition;

namespace FillMyADT.Tests;

public class WindowsEventSourceTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Fact]
    public void WhenMachineRunsAllDayThenBootShutdownAndDefaultLunchAreCreated()
    {
        var events = Create(("07:47", Transition.Wake), ("16:32", Transition.Sleep));

        Assert.Equal(
        [
            "07:47 Boot",
            "12:00 LunchBreakStart",
            "12:30 LunchBreakEnd",
            "16:32 Shutdown",
        ], events);
    }

    [Fact]
    public void WhenIdleGapInLunchWindowThenLunchIsTakenFromTheGap()
    {
        var events = Create(("07:47", Transition.Wake), ("11:50", Transition.Sleep), ("12:35", Transition.Wake), ("16:32", Transition.Sleep));

        Assert.Equal(
        [
            "07:47 Boot",
            "11:45 LunchBreakStart",
            "12:45 LunchBreakEnd",
            "16:32 Shutdown",
        ], events);
    }

    [Fact]
    public void WhenLongIdleGapDuringTheDayThenNoOffPeriodIsCreated()
    {
        var events = Create(("07:47", Transition.Wake), ("09:53", Transition.Sleep), ("14:21", Transition.Wake), ("17:34", Transition.Sleep));

        Assert.Equal(
        [
            "07:47 Boot",
            "12:00 LunchBreakStart",
            "12:30 LunchBreakEnd",
            "17:34 Shutdown",
        ], events);
    }

    [Fact]
    public void WhenLongIdleGapInTheEveningThenOffPeriodIsCreated()
    {
        var events = Create(("07:47", Transition.Wake), ("17:00", Transition.Sleep), ("20:00", Transition.Wake), ("21:00", Transition.Sleep));

        Assert.Equal(
        [
            "07:47 Boot",
            "12:00 LunchBreakStart",
            "12:30 LunchBreakEnd",
            "17:00 Shutdown",
            "20:00 Boot",
            "21:00 Shutdown",
        ], events);
    }

    [Fact]
    public void WhenShortEveningIdleGapThenNoOffPeriodIsCreated()
    {
        var events = Create(("07:47", Transition.Wake), ("17:00", Transition.Sleep), ("17:30", Transition.Wake), ("18:00", Transition.Sleep));

        Assert.Equal(
        [
            "07:47 Boot",
            "12:00 LunchBreakStart",
            "12:30 LunchBreakEnd",
            "18:00 Shutdown",
        ], events);
    }

    [Fact]
    public void WhenActivityBeforeSixThenItIsIgnored()
    {
        var events = Create(("03:00", Transition.Wake), ("03:20", Transition.Sleep), ("07:47", Transition.Wake), ("16:32", Transition.Sleep));

        Assert.Equal("07:47 Boot", events[0]);
    }

    private static List<string> Create(params (string Time, Transition Kind)[] transitions)
    {
        var source = new WindowsEventSource(new WindowsEventSourceConfig());
        return source.CreateEventsFromTransitions(transitions.Select(t =>
                (Monday.ToDateTime(TimeOnly.Parse(t.Time, System.Globalization.CultureInfo.InvariantCulture)), t.Kind)))
            .Select(Format)
            .ToList();
    }

    private static string Format(Event e) => $"{e.Timestamp:HH:mm} {e.EventType}";
}
