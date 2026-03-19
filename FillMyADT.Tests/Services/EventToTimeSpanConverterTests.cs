using FillMyADT.Models;
using FillMyADT.Services;
using Xunit;

namespace FillMyADT.Tests.Services;

public class EventToTimeSpanConverterTests
{
    private readonly EventToTimeSpanConverter _converter;

    public EventToTimeSpanConverterTests()
    {
        _converter = new EventToTimeSpanConverter();
    }

    [Fact]
    public void FirstSlot_RoundsUpTo5Min_EndsAtNextQuarterHour()
    {
        // Arrange: Boot at 7:12 AM
        var events = new List<Event>
        {
            CreateBootEvent(new DateTime(2024, 1, 15, 7, 12, 0)),
            CreateShutdownEvent(new DateTime(2024, 1, 15, 17, 0, 0))
        };

        // Act
        var slots = _converter.ConvertEventsToTimeSlots(events);

        // Assert
        Assert.NotEmpty(slots);
        var firstSlot = slots[0];
        Assert.Equal(new TimeOnly(7, 15), firstSlot.StartTime); // Rounded up to 5min
        Assert.Equal(new TimeOnly(7, 30), firstSlot.EndTime);   // Next quarter hour
    }

    [Fact]
    public void FirstSlot_WhenOnQuarterHour_HasMinimum15MinDuration()
    {
        // Arrange: Boot at exactly 7:00 AM
        var events = new List<Event>
        {
            CreateBootEvent(new DateTime(2024, 1, 15, 7, 0, 0)),
            CreateShutdownEvent(new DateTime(2024, 1, 15, 17, 0, 0))
        };

        // Act
        var slots = _converter.ConvertEventsToTimeSlots(events);

        // Assert
        var firstSlot = slots[0];
        Assert.Equal(new TimeOnly(7, 0), firstSlot.StartTime);
        Assert.Equal(new TimeOnly(7, 15), firstSlot.EndTime); // Extended to 15 min minimum
    }

    [Fact]
    public void FirstSlot_RoundsUpTo5Min_NotFullQuarterIfShort()
    {
        // Arrange: Boot at 7:03 AM
        var events = new List<Event>
        {
            CreateBootEvent(new DateTime(2024, 1, 15, 7, 3, 0)),
            CreateShutdownEvent(new DateTime(2024, 1, 15, 17, 0, 0))
        };

        // Act
        var slots = _converter.ConvertEventsToTimeSlots(events);

        // Assert
        var firstSlot = slots[0];
        Assert.Equal(new TimeOnly(7, 5), firstSlot.StartTime);  // Rounded up to 5min
        Assert.Equal(new TimeOnly(7, 15), firstSlot.EndTime);   // Next quarter hour
    }

    [Fact]
    public void LastSlot_RoundsDownTo5Min()
    {
        // Arrange: Shutdown at 17:18
        var events = new List<Event>
        {
            CreateBootEvent(new DateTime(2024, 1, 15, 7, 0, 0)),
            CreateCommitEvent(new DateTime(2024, 1, 15, 9, 30, 0), "Work on feature"),
            CreateShutdownEvent(new DateTime(2024, 1, 15, 17, 18, 0))
        };

        // Act
        var slots = _converter.ConvertEventsToTimeSlots(events);

        // Assert
        var lastSlot = slots[^1];
        Assert.Equal(new TimeOnly(17, 15), lastSlot.EndTime); // Rounded down to 5min
    }

    [Fact]
    public void OnlyOneNoonBreak_InsertedPerDay()
    {
        // Arrange: Full day work
        var events = new List<Event>
        {
            CreateBootEvent(new DateTime(2024, 1, 15, 7, 0, 0)),
            CreateCommitEvent(new DateTime(2024, 1, 15, 9, 30, 0), "Morning work"),
            CreateCommitEvent(new DateTime(2024, 1, 15, 14, 30, 0), "Afternoon work"),
            CreateShutdownEvent(new DateTime(2024, 1, 15, 17, 0, 0))
        };

        // Act
        var slots = _converter.ConvertEventsToTimeSlots(events);

        // Assert: Should have startup slot + morning work + afternoon work = 3 slots
        // With 30min gap from 12:00-12:30
        var morningWorkSlot = slots.FirstOrDefault(s => s.EndTime == new TimeOnly(12, 0));
        var afternoonWorkSlot = slots.FirstOrDefault(s => s.StartTime == new TimeOnly(12, 30));
        
        Assert.NotNull(morningWorkSlot);
        Assert.NotNull(afternoonWorkSlot);
        
        // Count noon gaps (slots that end at 12:00 or start at 12:30)
        var noonGaps = slots.Count(s => s.EndTime == new TimeOnly(12, 0) || s.StartTime == new TimeOnly(12, 30));
        Assert.Equal(2, noonGaps); // Should be exactly 2 (one morning ending, one afternoon starting)
    }

    [Fact]
    public void NoAfternoonEvents_ContinuesMorningActivity()
    {
        // Arrange: Only morning events
        var events = new List<Event>
        {
            CreateBootEvent(new DateTime(2024, 1, 15, 7, 0, 0)),
            CreateCommitEvent(new DateTime(2024, 1, 15, 9, 30, 0), "PROJ-123", "Morning work"),
            CreateShutdownEvent(new DateTime(2024, 1, 15, 17, 0, 0))
        };

        // Act
        var slots = _converter.ConvertEventsToTimeSlots(events);

        // Assert
        var morningSlot = slots.FirstOrDefault(s => s.EndTime == new TimeOnly(12, 0));
        var afternoonSlot = slots.FirstOrDefault(s => s.StartTime == new TimeOnly(12, 30));

        Assert.NotNull(morningSlot);
        Assert.NotNull(afternoonSlot);
        
        // Afternoon should continue with same ticket and text as morning
        Assert.Equal(morningSlot.TicketNr, afternoonSlot.TicketNr);
        Assert.Equal(morningSlot.Text, afternoonSlot.Text);
        Assert.Equal("PROJ-123", afternoonSlot.TicketNr);
    }

    [Fact]
    public void EventsOrderedByTimestamp()
    {
        // Arrange: Events in random order
        var events = new List<Event>
        {
            CreateCommitEvent(new DateTime(2024, 1, 15, 14, 30, 0), "Afternoon"),
            CreateBootEvent(new DateTime(2024, 1, 15, 7, 0, 0)),
            CreateCommitEvent(new DateTime(2024, 1, 15, 9, 30, 0), "Morning"),
            CreateShutdownEvent(new DateTime(2024, 1, 15, 17, 0, 0))
        };

        // Act
        var slots = _converter.ConvertEventsToTimeSlots(events);

        // Assert: Slots should be in chronological order
        for (int i = 0; i < slots.Count - 1; i++)
        {
            var current = slots[i];
            var next = slots[i + 1];
            
            // Each slot should start after or at the previous slot's end
            Assert.True(current.EndTime <= next.StartTime, 
                $"Slot {i} ends at {current.EndTime} but slot {i+1} starts at {next.StartTime}");
        }
    }

    [Fact]
    public void NoBootEvent_ReturnsEmptyList()
    {
        // Arrange: No boot event
        var events = new List<Event>
        {
            CreateCommitEvent(new DateTime(2024, 1, 15, 9, 30, 0), "Work"),
            CreateShutdownEvent(new DateTime(2024, 1, 15, 17, 0, 0))
        };

        // Act
        var slots = _converter.ConvertEventsToTimeSlots(events);

        // Assert
        Assert.Empty(slots);
    }

    [Fact]
    public void NoonBreak_IsExactly30Minutes()
    {
        // Arrange: Full day work
        var events = new List<Event>
        {
            CreateBootEvent(new DateTime(2024, 1, 15, 7, 0, 0)),
            CreateCommitEvent(new DateTime(2024, 1, 15, 10, 0, 0), "Morning"),
            CreateCommitEvent(new DateTime(2024, 1, 15, 14, 0, 0), "Afternoon"),
            CreateShutdownEvent(new DateTime(2024, 1, 15, 17, 0, 0))
        };

        // Act
        var slots = _converter.ConvertEventsToTimeSlots(events);

        // Assert
        var morningEnd = slots.FirstOrDefault(s => s.EndTime == new TimeOnly(12, 0));
        var afternoonStart = slots.FirstOrDefault(s => s.StartTime == new TimeOnly(12, 30));

        if (morningEnd != null && afternoonStart != null)
        {
            var gapDuration = afternoonStart.StartTime - morningEnd.EndTime;
            Assert.Equal(TimeSpan.FromMinutes(30), gapDuration);
        }
    }

    [Fact]
    public void FirstSlotHasStartupText()
    {
        // Arrange
        var events = new List<Event>
        {
            CreateBootEvent(new DateTime(2024, 1, 15, 7, 12, 0)),
            CreateShutdownEvent(new DateTime(2024, 1, 15, 17, 0, 0))
        };

        // Act
        var slots = _converter.ConvertEventsToTimeSlots(events);

        // Assert
        var firstSlot = slots[0];
        Assert.Equal("Startup", firstSlot.Text);
        Assert.Null(firstSlot.TicketNr);
    }

    [Fact]
    public void TicketNumber_ExtractedFromMetadata()
    {
        // Arrange
        var events = new List<Event>
        {
            CreateBootEvent(new DateTime(2024, 1, 15, 7, 0, 0)),
            CreateCommitEvent(new DateTime(2024, 1, 15, 9, 30, 0), "PROJ-456", "Feature implementation"),
            CreateShutdownEvent(new DateTime(2024, 1, 15, 17, 0, 0))
        };

        // Act
        var slots = _converter.ConvertEventsToTimeSlots(events);

        // Assert
        var workSlot = slots.FirstOrDefault(s => s.TicketNr != null);
        Assert.NotNull(workSlot);
        Assert.Equal("PROJ-456", workSlot.TicketNr);
    }

    // Helper methods to create test events
    private Event CreateBootEvent(DateTime timestamp)
    {
        return new Event
        {
            Source = "Windows Events",
            Timestamp = timestamp,
            EventType = "Boot",
            Description = "System started"
        };
    }

    private Event CreateShutdownEvent(DateTime timestamp)
    {
        return new Event
        {
            Source = "Windows Events",
            Timestamp = timestamp,
            EventType = "Shutdown",
            Description = "System shutdown"
        };
    }

    private Event CreateCommitEvent(DateTime timestamp, string description)
    {
        return new Event
        {
            Source = "Git History",
            Timestamp = timestamp,
            EventType = "Commit",
            Description = description
        };
    }

    private Event CreateCommitEvent(DateTime timestamp, string ticketNumber, string description)
    {
        return new Event
        {
            Source = "Git History",
            Timestamp = timestamp,
            EventType = "Commit",
            Description = description,
            Metadata = new Dictionary<string, string>
            {
                ["TicketNumber"] = ticketNumber
            }
        };
    }
}
