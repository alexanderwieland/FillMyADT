using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using FillMyADT.Models;
using FillMyADT.Models.Configuration;
using FillMyADT.Services.BrowserHistory;
using Serilog;

namespace FillMyADT.Services.EventSources;

/// <summary>
/// Event source that reads Microsoft Edge browser history
/// </summary>
public class EdgeEventSource : IEventSource
{
    private static readonly ILogger Log = Serilog.Log.ForContext<EdgeEventSource>();

    private readonly EdgeEventSourceConfig _config;
    private readonly List<CompiledTicketPattern> _compiledPatterns;

    public string Name => "Edge Browser";

    public EdgeEventSource(EdgeEventSourceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;

        // Compile regex patterns for performance
        _compiledPatterns = _config.TicketPatterns
            .Select(p => new CompiledTicketPattern(
                p.Name,
                new Regex(p.UrlPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase),
                p.TicketFormat))
            .ToList();

        Log.Information("EdgeEventSource initialized with {PatternCount} ticket patterns", _compiledPatterns.Count);
    }

    public async Task<IEnumerable<Event>> GetEventsAsync(
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default)
    {
        if (!_config.IsEnabled)
        {
            Log.Debug("EdgeEventSource is disabled");
            return [];
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Read browser history from SQLite database
            var visits = await EdgeHistoryReader.ReadHistoryAsync(
                _config.ProfilePath,
                startDate,
                endDate,
                _config.MaxVisitsPerQuery,
                cancellationToken);

            // Filter visits by domain and duration
            var filteredVisits = FilterVisits(visits);

            // Extract tickets and create events
            var events = ProcessVisits(filteredVisits);

            stopwatch.Stop();
            Log.Information("EdgeEventSource: Found {EventCount} events from {VisitCount} visits in {ElapsedMs}ms",
                events.Count, filteredVisits.Count, stopwatch.ElapsedMilliseconds);

            return events.OrderBy(e => e.Timestamp);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "EdgeEventSource: Error reading browser history");
            return [];
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var historyPath = Path.Combine(_config.ProfilePath, "History");
        var isAvailable = File.Exists(historyPath);
        
        if (!isAvailable)
        {
            Log.Warning("Edge history database not found at {Path}", historyPath);
        }
        
        return Task.FromResult(isAvailable);
    }

    private List<BrowserVisit> FilterVisits(List<BrowserVisit> visits)
    {
        var filtered = visits.Where(v =>
        {
            // Filter by minimum duration
            if (v.VisitDurationSeconds < _config.MinVisitDurationSeconds)
                return false;

            // Filter by included domains
            if (_config.IncludeDomains.Count > 0)
            {
                var matchesDomain = _config.IncludeDomains.Any(domain =>
                    v.Url.Contains(domain, StringComparison.OrdinalIgnoreCase));
                
                if (!matchesDomain)
                    return false;
            }

            // Filter by excluded domains
            if (_config.ExcludeDomains.Count > 0)
            {
                var matchesExcluded = _config.ExcludeDomains.Any(domain =>
                    v.Url.Contains(domain, StringComparison.OrdinalIgnoreCase));
                
                if (matchesExcluded)
                    return false;
            }

            return true;
        }).ToList();

        Log.Debug("Filtered {FilteredCount} of {TotalCount} visits", filtered.Count, visits.Count);
        return filtered;
    }

    private List<Event> ProcessVisits(List<BrowserVisit> visits)
    {
        var events = new List<Event>();

        if (visits.Count == 0)
            return events;

        // Extract ticket info from all visits
        var visitTickets = visits
            .Select(v =>
            {
                var source = DetermineTicketSource(v.Url);
                var titleTicket = v.Title != null ? ExtractTicketFromTitle(v.Title) : null;
                var urlTicket = ExtractTicketNumberFromUrl(v.Url);

                return new VisitWithTicket
                {
                    Visit = v,
                    Source = source,
                    TitleBasedTicket = titleTicket,
                    UrlBasedTicket = urlTicket
                };
            })
            .Where(x => x.Source != TicketSource.Unknown && 
                       (!string.IsNullOrEmpty(x.TitleBasedTicket) || !string.IsNullOrEmpty(x.UrlBasedTicket)))
            .ToList();

        // Separate Redmine and TFS visits
        var redmineVisits = visitTickets.Where(x => x.Source == TicketSource.Redmine).ToList();

        // For TFS: Only include visits with valid ticket numbers (#xxxxx format from title)
        var tfsVisits = visitTickets
            .Where(x => x.Source == TicketSource.TFS && !string.IsNullOrEmpty(x.TitleBasedTicket))
            .ToList();

        var ignoredTfsCount = visitTickets.Count(x => x.Source == TicketSource.TFS && string.IsNullOrEmpty(x.TitleBasedTicket));
        if (ignoredTfsCount > 0)
        {
            Log.Information("Ignored {Count} TFS visits without valid #xxxxx ticket numbers", ignoredTfsCount);
        }

        Log.Information("Processing {TotalCount} visits: {RedmineCount} Redmine, {TfsCount} TFS",
            visitTickets.Count, redmineVisits.Count, tfsVisits.Count);

        // Process Redmine: Group into half-day timeslots
        events.AddRange(ProcessRedmineVisits(redmineVisits));

        // Process TFS: Individual tickets with time ranges
        events.AddRange(ProcessTfsVisits(tfsVisits));

        return events;
    }

    private List<Event> ProcessRedmineVisits(List<VisitWithTicket> redmineVisits)
    {
        var events = new List<Event>();

        if (redmineVisits.Count == 0)
            return events;

        // Group by date and half-day (AM/PM)
        var halfDayGroups = redmineVisits
            .GroupBy(v => new
            {
                Date = v.Visit.VisitTime.Date,
                HalfDay = v.Visit.VisitTime.Hour < 12 ? "AM" : "PM"
            })
            .ToList();

        foreach (var group in halfDayGroups)
        {
            var distinctTickets = group.Select(v => v.TitleBasedTicket ?? v.UrlBasedTicket!).Distinct().ToList();
            var ticketCount = distinctTickets.Count;

            // Determine duration based on ticket count
            var durationMinutes = ticketCount < 5 ? 15 : 30;

            // Use earliest visit time in this half-day
            var earliestVisit = group.OrderBy(v => v.Visit.VisitTime).First().Visit;

            // Collect all visit details
            var allUrls = group.Select(v => v.Visit.Url).Distinct().ToList();
            var totalVisits = group.Count();

            // Create START and END events (like calendar meetings)
            var startEndEvents = CreateRedmineGroupEventPair(
                earliestVisit,
                distinctTickets,
                durationMinutes,
                totalVisits,
                allUrls,
                group.Key.HalfDay);

            events.AddRange(startEndEvents);

            Log.Debug("Redmine {HalfDay} group on {Date}: {TicketCount} tickets, {Duration}min, {VisitCount} visits",
                group.Key.HalfDay, group.Key.Date.ToShortDateString(), ticketCount, durationMinutes, totalVisits);
        }

        return events;
    }

    private List<Event> ProcessTfsVisits(List<VisitWithTicket> tfsVisits)
    {
        var events = new List<Event>();

        if (tfsVisits.Count == 0)
            return events;

        // Group by date and URL-based ticket (for consistency across different page titles)
        var ticketGroups = tfsVisits
            .GroupBy(v => new { Date = v.Visit.VisitTime.Date, GroupingTicket = v.UrlBasedTicket! })
            .ToList();

        foreach (var ticketGroup in ticketGroups)
        {
            var visitsForTicket = ticketGroup.ToList();

            // Prefer work item number (#xxxxx) from title if available, otherwise use URL-based ticket (PR-xxx)
            var displayTicket = visitsForTicket
                .Select(v => v.TitleBasedTicket)
                .Where(t => !string.IsNullOrEmpty(t))
                .FirstOrDefault() ?? ticketGroup.Key.GroupingTicket;

            // Get ordered visits for this ticket
            var orderedVisits = visitsForTicket.OrderBy(v => v.Visit.VisitTime).ToList();
            var firstVisit = orderedVisits.First().Visit;
            var lastVisit = orderedVisits.Last().Visit;

            // Collect URLs and title
            var urls = visitsForTicket.Select(v => v.Visit.Url).Distinct().ToList();
            var title = visitsForTicket
                .Select(v => v.Visit.Title)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .OrderByDescending(t => t!.Length)
                .FirstOrDefault();

            // Check if visits are consecutive (within configured gap threshold)
            var areVisitsConsecutive = AreVisitsConsecutive(
                orderedVisits.Select(v => v.Visit).ToList(), 
                _config.TfsMaxGapMinutes);

            BrowserVisit endVisit;
            if (areVisitsConsecutive && orderedVisits.Count > 1)
            {
                // Use actual time range for consecutive visits
                endVisit = lastVisit;
                Log.Debug("TFS ticket {Ticket}: Using time range (consecutive visits)", displayTicket);
            }
            else
            {
                // Use configured fixed duration for non-consecutive or single visits
                endVisit = new BrowserVisit
                {
                    VisitTime = firstVisit.VisitTime.AddMinutes(_config.TfsFixedDurationMinutes),
                    VisitDurationMicroseconds = 0,
                    Url = firstVisit.Url,
                    Title = firstVisit.Title,
                    VisitCount = firstVisit.VisitCount
                };
                Log.Debug("TFS ticket {Ticket}: Using fixed {Duration}min duration (gap detected or single visit)", 
                    displayTicket, _config.TfsFixedDurationMinutes);
            }

            // Create START and END events (like calendar meetings)
            var startEndEvents = CreateTfsTicketEventPair(
                firstVisit,
                endVisit,
                displayTicket!,
                visitsForTicket.Count,
                urls,
                title);

            events.AddRange(startEndEvents);

            Log.Debug("TFS ticket {Ticket} on {Date}: {Start} - {End}, {VisitCount} visits",
                displayTicket, ticketGroup.Key.Date.ToShortDateString(),
                firstVisit.VisitTime.ToString("HH:mm"), endVisit.VisitTime.ToString("HH:mm"),
                visitsForTicket.Count);
        }

        return events;
    }

    /// <summary>
    /// Check if visits are consecutive (no large gaps between them)
    /// </summary>
    private static bool AreVisitsConsecutive(List<BrowserVisit> orderedVisits, int maxGapMinutes)
    {
        if (orderedVisits.Count <= 1)
            return false;

        for (int i = 1; i < orderedVisits.Count; i++)
        {
            var gap = (orderedVisits[i].VisitTime - orderedVisits[i - 1].VisitTime).TotalMinutes;
            if (gap > maxGapMinutes)
            {
                return false; // Found a gap larger than threshold
            }
        }

        return true; // All visits are within threshold
    }

    private List<Event> CreateRedmineGroupEventPair(
        BrowserVisit earliestVisit,
        List<string> tickets,
        int durationMinutes,
        int totalVisits,
        List<string> urls,
        string halfDay)
    {
        var ticketList = string.Join(", ", tickets.OrderBy(t => t));
        var description = $"Redmine Tickets ({tickets.Count} tickets)";

        var metadata = new Dictionary<string, string>
        {
            ["TicketNumber"] = ticketList,
            ["TicketCount"] = tickets.Count.ToString(),
            ["VisitCount"] = totalVisits.ToString(),
            ["Duration"] = durationMinutes.ToString(),
            ["HalfDay"] = halfDay,
            ["Domain"] = "redmine.mp2.at"
        };

        // Add first few URLs as reference
        for (int i = 0; i < Math.Min(urls.Count, 5); i++)
        {
            metadata[$"Url{i + 1}"] = urls[i];
        }

        // Round start time to nearest 15 minutes
        var startTime = TimeOnly.FromDateTime(earliestVisit.VisitTime);
        var roundedStartTime = RoundToNearest15Minutes(startTime);
        var roundedStartDateTime = earliestVisit.VisitTime.Date.Add(roundedStartTime.ToTimeSpan());

        var startEvent = new Event
        {
            Source = Name,
            Timestamp = roundedStartDateTime,
            EventType = "CalendarMeetingStart",
            Description = description,
            Metadata = new Dictionary<string, string>(metadata)
            {
                ["Location"] = "Redmine"
            }
        };

        var endEvent = new Event
        {
            Source = Name,
            Timestamp = roundedStartDateTime.AddMinutes(durationMinutes),
            EventType = "CalendarMeetingEnd",
            Description = description,
            Metadata = new Dictionary<string, string>(metadata)
        };

        return [startEvent, endEvent];
    }

    private List<Event> CreateTfsTicketEventPair(
        BrowserVisit firstVisit,
        BrowserVisit lastVisit,
        string ticketNumber,
        int visitCount,
        List<string> urls,
        string? title)
    {
        var domain = ExtractDomain(firstVisit.Url);

        // Use title if available, otherwise use ticket number
        var description = _config.IncludePageTitle && !string.IsNullOrWhiteSpace(title)
            ? $"{ticketNumber}: {title}"
            : ticketNumber;

        // Round times to nearest 15 minutes
        var startTime = TimeOnly.FromDateTime(firstVisit.VisitTime);
        var endTime = TimeOnly.FromDateTime(lastVisit.VisitTime);

        var roundedStartTime = RoundToNearest15Minutes(startTime);
        var roundedEndTime = RoundToNearest15Minutes(endTime);

        // Ensure end is after start
        if (roundedEndTime <= roundedStartTime)
        {
            roundedEndTime = roundedStartTime.Add(TimeSpan.FromMinutes(15));
        }

        var roundedStartDateTime = firstVisit.VisitTime.Date.Add(roundedStartTime.ToTimeSpan());
        var roundedEndDateTime = lastVisit.VisitTime.Date.Add(roundedEndTime.ToTimeSpan());

        // Calculate duration in minutes
        var durationMinutes = (int)(roundedEndDateTime - roundedStartDateTime).TotalMinutes;

        var metadata = new Dictionary<string, string>
        {
            ["TicketNumber"] = ticketNumber,
            ["Domain"] = domain,
            ["VisitCount"] = visitCount.ToString(),
            ["StartTime"] = roundedStartDateTime.ToString("HH:mm:ss"),
            ["EndTime"] = roundedEndDateTime.ToString("HH:mm:ss"),
            ["Duration"] = durationMinutes.ToString()
        };

        // Add first few URLs as reference
        for (int i = 0; i < Math.Min(urls.Count, 3); i++)
        {
            metadata[$"Url{i + 1}"] = urls[i];
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            metadata["PageTitle"] = title;
        }

        // ALL TFS/Edge browser events use ReviewStart/ReviewEnd
        var startEvent = new Event
        {
            Source = Name,
            Timestamp = roundedStartDateTime,
            EventType = "ReviewStart",
            Description = description,
            Metadata = new Dictionary<string, string>(metadata)
            {
                ["Location"] = "TFS"
            }
        };

        var endEvent = new Event
        {
            Source = Name,
            Timestamp = roundedEndDateTime,
            EventType = "ReviewEnd",
            Description = description,
            Metadata = new Dictionary<string, string>(metadata)
        };

        return [startEvent, endEvent];
    }

    private TicketSource DetermineTicketSource(string url)
    {
        if (url.Contains("redmine.mp2.at", StringComparison.OrdinalIgnoreCase))
            return TicketSource.Redmine;

        if (url.Contains("tfs.mp2.at", StringComparison.OrdinalIgnoreCase))
            return TicketSource.TFS;

        return TicketSource.Unknown;
    }

    /// <summary>
    /// Extract ticket number from page title (only #XXXXX format is valid)
    /// </summary>
    private static string? ExtractTicketFromTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        // Only look for work item pattern: #12345
        var workItemMatch = Regex.Match(title, @"#(\d+)");
        if (workItemMatch.Success)
        {
            return workItemMatch.Groups[1].Value;
        }

        // No valid ticket format found
        return null;
    }

    /// <summary>
    /// Extract ticket number from URL only (for consistent grouping)
    /// </summary>
    private string? ExtractTicketNumberFromUrl(string url)
    {
        foreach (var pattern in _compiledPatterns)
        {
            var match = pattern.Regex.Match(url);
            if (match.Success)
            {
                var ticket = pattern.TicketFormat;
                for (int i = 1; i < match.Groups.Count; i++)
                {
                    ticket = ticket.Replace($"${i}", match.Groups[i].Value);
                }
                return ticket;
            }
        }
        return null;
    }

    private static string ExtractDomain(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Round time to nearest 15-minute interval (00, 15, 30, 45)
    /// </summary>
    private static TimeOnly RoundToNearest15Minutes(TimeOnly time)
    {
        var totalMinutes = time.Hour * 60 + time.Minute;
        var roundedMinutes = (int)Math.Round(totalMinutes / 15.0) * 15;

        if (roundedMinutes >= 24 * 60)
        {
            roundedMinutes = 24 * 60 - 15;
        }

        return new TimeOnly(roundedMinutes / 60, roundedMinutes % 60);
    }

    private record CompiledTicketPattern(string Name, Regex Regex, string TicketFormat);

    private enum TicketSource
    {
        Unknown,
        Redmine,
        TFS
    }

    private class VisitWithTicket
    {
        public required BrowserVisit Visit { get; init; }
        public TicketSource Source { get; init; }
        public string? TitleBasedTicket { get; init; }
        public string? UrlBasedTicket { get; init; }
    }
}
