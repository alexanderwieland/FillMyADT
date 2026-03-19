using System.IO;
using Serilog;
using Microsoft.Data.Sqlite;

namespace FillMyADT.Services.BrowserHistory;

/// <summary>
/// Reads browser history from Edge SQLite database
/// </summary>
internal class EdgeHistoryReader
{
    private const long ChromeEpochMicroseconds = 11644473600000000; // Microseconds from 1601-01-01 to 1970-01-01

    /// <summary>
    /// Read browser history visits from Edge database
    /// </summary>
    public static async Task<List<BrowserVisit>> ReadHistoryAsync(
        string profilePath,
        DateTime startDate,
        DateTime endDate,
        int maxVisits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profilePath);

        var historyDbPath = Path.Combine(profilePath, "History");
        
        if (!File.Exists(historyDbPath))
        {
            Log.Warning("Edge history database not found at {Path}", historyDbPath);
            return [];
        }

        // Copy database to temp file to avoid locking issues
        var tempDbPath = await CopyDatabaseToTempAsync(historyDbPath, cancellationToken);

        try
        {
            return await ReadFromDatabaseAsync(tempDbPath, startDate, endDate, maxVisits, cancellationToken);
        }
        finally
        {
            // Clean up temp file
            try
            {
                if (File.Exists(tempDbPath))
                {
                    File.Delete(tempDbPath);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to delete temp database file {Path}", tempDbPath);
            }
        }
    }

    private static async Task<string> CopyDatabaseToTempAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"edge_history_{Guid.NewGuid():N}.db");
        
        await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var destStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        
        await sourceStream.CopyToAsync(destStream, cancellationToken);
        
        Log.Debug("Copied Edge history database to temp file: {TempPath}", tempPath);
        return tempPath;
    }

    private static async Task<List<BrowserVisit>> ReadFromDatabaseAsync(
        string dbPath,
        DateTime startDate,
        DateTime endDate,
        int maxVisits,
        CancellationToken cancellationToken)
    {
        var visits = new List<BrowserVisit>();
        
        var startTimestamp = DateTimeToChromiumTimestamp(startDate);
        var endTimestamp = DateTimeToChromiumTimestamp(endDate);

        var connectionString = $"Data Source={dbPath};Mode=ReadOnly";

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Query visits with URL and title information
        var query = @"
            SELECT 
                v.visit_time,
                v.visit_duration,
                u.url,
                u.title,
                u.visit_count
            FROM visits v
            INNER JOIN urls u ON v.url = u.id
            WHERE v.visit_time >= @StartTime 
              AND v.visit_time <= @EndTime
            ORDER BY v.visit_time DESC
            LIMIT @MaxVisits";

        await using var command = new SqliteCommand(query, connection);
        command.Parameters.AddWithValue("@StartTime", startTimestamp);
        command.Parameters.AddWithValue("@EndTime", endTimestamp);
        command.Parameters.AddWithValue("@MaxVisits", maxVisits);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var visitTime = ChromiumTimestampToDateTime(reader.GetInt64(0));
            var visitDuration = reader.GetInt64(1);
            var url = reader.GetString(2);
            var title = reader.IsDBNull(3) ? null : reader.GetString(3);
            var visitCount = reader.GetInt32(4);

            visits.Add(new BrowserVisit
            {
                VisitTime = visitTime,
                VisitDurationMicroseconds = visitDuration,
                Url = url,
                Title = title,
                VisitCount = visitCount
            });
        }

        Log.Debug("Read {Count} visits from Edge history database", visits.Count);
        return visits;
    }

    /// <summary>
    /// Convert DateTime to Chromium timestamp (microseconds since 1601-01-01)
    /// </summary>
    private static long DateTimeToChromiumTimestamp(DateTime dateTime)
    {
        var unixTimestamp = ((DateTimeOffset)dateTime.ToUniversalTime()).ToUnixTimeMilliseconds() * 1000;
        return unixTimestamp + ChromeEpochMicroseconds;
    }

    /// <summary>
    /// Convert Chromium timestamp to DateTime
    /// </summary>
    private static DateTime ChromiumTimestampToDateTime(long chromiumTimestamp)
    {
        var unixMicroseconds = chromiumTimestamp - ChromeEpochMicroseconds;
        var unixMilliseconds = unixMicroseconds / 1000;
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).LocalDateTime;
    }
}

/// <summary>
/// Represents a single browser visit
/// </summary>
internal record BrowserVisit
{
    public required DateTime VisitTime { get; init; }
    public required long VisitDurationMicroseconds { get; init; }
    public required string Url { get; init; }
    public string? Title { get; init; }
    public int VisitCount { get; init; }

    public int VisitDurationSeconds => (int)(VisitDurationMicroseconds / 1_000_000);
}
