using System.Text.RegularExpressions;

namespace FillMyADT.Services;

/// <summary>
/// Centralized ticket number extraction used across all event sources.
/// All methods return a bare ticket string with no leading '#', so callers
/// that prefix '#' in the UI will always produce exactly one '#'.
/// </summary>
public static class TicketParser
{
    // Matches #12345 (exactly 5 digits) in commit messages, branch names, and page titles.
    // Group 1 captures only the digits.
    private static readonly Regex GitPattern =
        new(@"#(\d{5})", RegexOptions.Compiled);

    // URL patterns for ticket extraction from browser history.
    // TFS URLs are intentionally excluded: the numbers in TFS URLs are internal TFS identifiers,
    // not ticket numbers. TFS ticket numbers are extracted from page titles only.
    private static readonly (Regex Pattern, string Format)[] UrlPatterns =
    [
        // Redmine: https://redmine.mp2.at/issues/12345
        (new Regex(@"redmine\.mp2\.at/issues/(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "$1"),
    ];

    /// <summary>
    /// Extracts a ticket number from a Git commit message or branch name.
    /// Looks for the pattern <c>#12345</c> (exactly 5 digits) and returns the bare digits.
    /// Returns <see langword="null"/> when no ticket is found.
    /// </summary>
    public static string? FromGitText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = GitPattern.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Extracts a ticket number from a browser page title.
    /// Looks for the pattern <c>#12345</c> (exactly 5 digits) and returns the bare digits.
    /// Returns <see langword="null"/> when no ticket is found.
    /// </summary>
    public static string? FromBrowserTitle(string? title) => FromGitText(title);

    /// <summary>
    /// Extracts a ticket number from a browser URL using known URL patterns (Redmine only).
    /// TFS ticket numbers are always extracted from page titles, not URLs.
    /// Returns the ticket string (e.g. <c>"12345"</c>), or <see langword="null"/> when no pattern matches.
    /// </summary>
    public static string? FromBrowserUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        foreach (var (pattern, format) in UrlPatterns)
        {
            var match = pattern.Match(url);
            if (!match.Success)
                continue;

            var ticket = format;
            for (var i = 1; i < match.Groups.Count; i++)
                ticket = ticket.Replace($"${i}", match.Groups[i].Value);

            return ticket.TrimStart('#');
        }

        return null;
    }

    /// <summary>
    /// Ensures every ticket number in a single or comma-separated ticket string has a leading '#'.
    /// Returns <see langword="null"/> when <paramref name="ticket"/> is null or whitespace.
    /// </summary>
    public static string? EnsureHashPrefix(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return ticket;

        if (!ticket.Contains(','))
            return ticket.StartsWith('#') ? ticket : $"#{ticket}";

        return string.Join(", ", ticket
            .Split(',')
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t.StartsWith('#') ? t : $"#{t}"));
    }
}
