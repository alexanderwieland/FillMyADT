using FillMyADT.Models;
using FillMyADT.Models.Configuration;
using FillMyADT.Services.BrowserHistory;
using Serilog;
using System.Diagnostics;
using System.IO;

namespace FillMyADT.Services.EventSources;

/// <summary>
/// Event source that reads Microsoft Edge browser history
/// </summary>
public class EdgeEventSource : IEventSource
{
    private static readonly ILogger Log = Serilog.Log.ForContext<EdgeEventSource>();

    private const string RedmineDomain = "redmine.mp2.at";
    private const string TfsDomain = "tfs.mp2.at";

    private readonly EdgeEventSourceConfig _config;

    public string Name => "Edge Browser";

    public EdgeEventSource(EdgeEventSourceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        Log.Information("EdgeEventSource initialized");
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
        bool UrlMatches(BrowserVisit v, List<string> domains) =>
            domains.Any(d => v.Url.Contains(d, StringComparison.OrdinalIgnoreCase));

        var filtered = visits.Where(v =>
            v.VisitDurationSeconds >= _config.MinVisitDurationSeconds
            && (_config.IncludeDomains.Count == 0 || UrlMatches(v, _config.IncludeDomains))
            && !UrlMatches(v, _config.ExcludeDomains)).ToList();

        Log.Debug("Filtered {FilteredCount} of {TotalCount} visits", filtered.Count, visits.Count);
        return filtered;
    }
	private List<Event> ProcessVisits(List<BrowserVisit> visits)
	{
		var redmine = new List<(BrowserVisit Visit, string Ticket)>();
		var tfs = new List<(BrowserVisit Visit, string Ticket)>();

		foreach (var visit in visits)
		{
			if (visit.Url.Contains(RedmineDomain, StringComparison.OrdinalIgnoreCase))
			{
				var ticket = TicketParser.FromBrowserTitle(visit.Title) ?? TicketParser.FromBrowserUrl(visit.Url);
				if (ticket != null)
					redmine.Add((visit, ticket));
			}
			else if (visit.Url.Contains(TfsDomain, StringComparison.OrdinalIgnoreCase))
			{
				// TFS URL numbers are internal ids; only the page title carries the ticket number
				var ticket = TicketParser.FromBrowserTitle(visit.Title);
				if (ticket != null)
					tfs.Add((visit, ticket));
			}
		}

		Log.Information("Processing visits: {RedmineCount} Redmine, {TfsCount} TFS", redmine.Count, tfs.Count);

		return [.. ProcessRedmineVisits(redmine), .. ProcessTfsVisits(tfs)];
	}

	/// <summary>
	/// Redmine visits are summarized per half-day: one block whose length depends on how many tickets were viewed
	/// </summary>
	private IEnumerable<Event> ProcessRedmineVisits(List<(BrowserVisit Visit, string Ticket)> visits)
	{
		foreach (var group in visits.GroupBy(v => (v.Visit.VisitTime.Date, HalfDay: v.Visit.VisitTime.Hour < 12 ? "AM" : "PM")))
		{
			var tickets = group.Select(v => v.Ticket).Distinct().Order().ToList();
			var urls = group.Select(v => v.Visit.Url).Distinct().Take(5).ToList();
			var durationMinutes = tickets.Count < 5 ? 15 : 30;
			var start = RoundNearest(group.Min(v => v.Visit.VisitTime));

			var metadata = new Dictionary<string, string>
			{
				["TicketNumber"] = string.Join(", ", tickets.Select(t => $"#{t}")),
				["TicketCount"] = tickets.Count.ToString(),
				["VisitCount"] = group.Count().ToString(),
				["Duration"] = durationMinutes.ToString(),
				["HalfDay"] = group.Key.HalfDay,
				["Domain"] = RedmineDomain
			};
			AddUrls(metadata, urls);

			foreach (var e in CreateEventPair(start, start.AddMinutes(durationMinutes),
				EventType.TicketStart, EventType.TicketEnd, $"Redmine Tickets ({tickets.Count} tickets)", "Redmine", metadata))
				yield return e;
		}
	}

	/// <summary>
	/// TFS visits are grouped per ticket and day. Each 15-minute block containing at least one visit counts
	/// as worked; adjacent blocks merge into one slot. Tickets touched in fewer than
	/// <see cref="EdgeEventSourceConfig.TfsMinBlocks"/> blocks per day are treated as glances and dropped.
	/// Browser dwell time is ignored because an open tab does not mean active work.
	/// </summary>
	private IEnumerable<Event> ProcessTfsVisits(List<(BrowserVisit Visit, string Ticket)> visits)
	{
		foreach (var group in visits.GroupBy(v => (v.Visit.VisitTime.Date, v.Ticket)))
		{
			var ticket = group.Key.Ticket;
			var title = group
				.Select(v => v.Visit.Title)
				.Where(t => !string.IsNullOrWhiteSpace(t))
				.MaxBy(t => t!.Length);
			var description = _config.IncludePageTitle && title != null ? $"#{ticket}: {title}" : $"#{ticket}";

			var blocks = group
				.Select(v => v.Visit)
				.GroupBy(v => FloorToBlock(v.VisitTime))
				.OrderBy(b => b.Key)
				.ToList();

			if (blocks.Count < _config.TfsMinBlocks)
			{
				Log.Debug("TFS ticket {Ticket} on {Date}: ignored as glance ({Blocks} block)", ticket, group.Key.Date.ToShortDateString(), blocks.Count);
				continue;
			}

			foreach (var run in SplitIntoRuns(blocks))
			{
				var session = run.SelectMany(b => b).ToList();
				var start = run[0].Key;
				var end = run[^1].Key.AddMinutes(BlockMinutes);

				var metadata = new Dictionary<string, string>
				{
					["TicketNumber"] = ticket,
					["Domain"] = Uri.TryCreate(session[0].Url, UriKind.Absolute, out var uri) ? uri.Host : "unknown",
					["VisitCount"] = session.Count.ToString(),
					["StartTime"] = start.ToString("HH:mm:ss"),
					["EndTime"] = end.ToString("HH:mm:ss"),
					["Duration"] = ((int)(end - start).TotalMinutes).ToString()
				};
				AddUrls(metadata, session.Select(v => v.Url).Distinct().Take(3).ToList());
				if (title != null)
					metadata["PageTitle"] = title;

				foreach (var e in CreateEventPair(start, end, EventType.ReviewStart, EventType.ReviewEnd, description, "TFS", metadata))
					yield return e;
			}
		}
	}

	private const int BlockMinutes = 15;

	private static DateTime FloorToBlock(DateTime time) =>
		time.Date.AddMinutes((int)time.TimeOfDay.TotalMinutes / BlockMinutes * BlockMinutes);

	/// <summary>
	/// Split ordered blocks into runs of directly adjacent blocks
	/// </summary>
	private static List<List<IGrouping<DateTime, BrowserVisit>>> SplitIntoRuns(List<IGrouping<DateTime, BrowserVisit>> orderedBlocks)
	{
		var runs = new List<List<IGrouping<DateTime, BrowserVisit>>>();
		foreach (var block in orderedBlocks)
		{
			if (runs.Count == 0 || block.Key != runs[^1][^1].Key.AddMinutes(BlockMinutes))
				runs.Add([]);
			runs[^1].Add(block);
		}
		return runs;
	}

	private Event[] CreateEventPair(
		DateTime start, DateTime end, EventType startType, EventType endType,
		string description, string category, Dictionary<string, string> metadata) =>
	[
		new Event
		{
			Source = Name,
			Timestamp = start,
			EventType = startType,
			Description = description,
			Metadata = new Dictionary<string, string>(metadata) { ["Category"] = category }
		},
		new Event
		{
			Source = Name,
			Timestamp = end,
			EventType = endType,
			Description = description,
			Metadata = metadata
		}
	];

	private static void AddUrls(Dictionary<string, string> metadata, List<string> urls)
	{
		for (var i = 0; i < urls.Count; i++)
			metadata[$"Url{i + 1}"] = urls[i];
	}

	private static DateTime RoundNearest(DateTime time) =>
		time.Date.Add(TimeHelper.RoundNearest(TimeOnly.FromDateTime(time), 15).ToTimeSpan());
}
