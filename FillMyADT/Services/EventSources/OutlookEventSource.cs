using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FillMyADT.Models;
using FillMyADT.Models.Configuration;
using Serilog;

namespace FillMyADT.Services.EventSources;

/// <summary>
/// Event source that reads calendar appointments from Outlook via COM Interop
/// </summary>
[SupportedOSPlatform("windows")]
public class OutlookEventSource : IEventSource
{
    private static readonly ILogger Log = Serilog.Log.ForContext<OutlookEventSource>();

    private readonly OutlookEventSourceConfig _config;
    private readonly AppConfiguration _appConfig;
    private readonly string _homeOfficeEventName;
    private readonly string _holidayEventName;
    private readonly string _zeitausgleichEventName;

    public string Name => "Outlook Calendar";

    public OutlookEventSource(OutlookEventSourceConfig config, AppConfiguration appConfig)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(appConfig);

        _config = config;
        _appConfig = appConfig;

        // Construct full event names from initials and suffixes
        _homeOfficeEventName = $"{appConfig.Initials} - {config.HomeOfficeSuffix}";
        _holidayEventName = $"{appConfig.Initials} - {config.HolidaySuffix}";
        _zeitausgleichEventName = $"{appConfig.Initials} - {config.ZeitausgleichSuffix}";
    }

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    private static object? TryGetActiveOutlook()
    {
        try
        {
            var outlookClsid = new Guid("0006F03A-0000-0000-C000-000000000046");
            GetActiveObject(ref outlookClsid, IntPtr.Zero, out var outlook);
            
            try
            {
                dynamic dynamicOutlook = outlook;
                var version = dynamicOutlook.Version;
                Log.Information("Connected to running Outlook version: {Version}", version);
            }
            catch
            {
                // Ignore version check errors
            }
            
            return outlook;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IEnumerable<Event>> GetEventsAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var events = new List<Event>();

            try
            {
                dynamic? outlook = null;
                dynamic? ns = null;
                dynamic? calendarFolder = null;
                dynamic? items = null;

                try
                {
                    outlook = TryGetActiveOutlook();
                    if (outlook == null)
                    {
                        if (_config.RequireRunningInstance)
                        {
                            Log.Warning("Outlook is not running. Please start classic Outlook Desktop (Office 16) before using this feature.");
                            return events;
                        }

                        Log.Debug("Outlook is not running, attempting to start");
                        var outlookType = Type.GetTypeFromProgID("Outlook.Application");
                        if (outlookType == null)
                        {
                            Log.Warning("Outlook is not installed");
                            return events;
                        }

                        outlook = Activator.CreateInstance(outlookType);
                        if (outlook == null)
                        {
                            Log.Warning("Failed to create Outlook instance");
                            return events;
                        }

                        try
                        {
                            dynamic dynamicOutlook = outlook;
                            var version = dynamicOutlook.Version;
                            Log.Information("Started Outlook version: {Version}", version);
                        }
                        catch
                        {
                            // Ignore version check errors
                        }
                    }

                    ns = outlook.GetNamespace("MAPI");
                    calendarFolder = ns.GetDefaultFolder(9); // 9 = olFolderCalendar

                    items = calendarFolder.Items;
                    items.Sort("[Start]");
                    items.IncludeRecurrences = true;

                    // Use proper range filter: events that start before endDate+1day AND end after startDate
                    // This properly handles all-day events which have End set to midnight of the next day
                    var endDatePlusOne = endDate.AddDays(1);
                    var filter = $"[Start] < '{endDatePlusOne:g}' AND [End] > '{startDate:g}'";
                    var restrictedItems = items.Restrict(filter);

                    Log.Debug("Outlook filter: [Start] < '{EndDatePlusOne:g}' AND [End] > '{StartDate:g}'", endDatePlusOne, startDate);

                    foreach (dynamic item in restrictedItems)
                    {
                        try
                        {
                            if (cancellationToken.IsCancellationRequested)
                                break;

                            DateTime itemStart = item.Start;
                            DateTime itemEnd = item.End;
                            string subject = item.Subject ?? "No Subject";
                            string location = item.Location ?? string.Empty;
                            bool isAllDay = item.AllDayEvent;

                            // Log all-day events for debugging
                            if (isAllDay)
                            {
                                Log.Debug("Found all-day event: '{Subject}' (EnableSpecialWholeDayEvents={Enable})", 
                                    subject, _config.EnableSpecialWholeDayEvents);
                            }

                            // Check for special whole-day events (HO, Urlaub, and Zeitausgleich)
                            if (isAllDay && _config.EnableSpecialWholeDayEvents && IsSpecialWholeDayEvent(subject, out var specialEventType))
                            {
                                // Create a single special event instead of start/end pair
                                events.Add(new Event
                                {
                                    Source = Name,
                                    Timestamp = itemStart.Date, // Use date only for whole-day events
                                    EventType = specialEventType,
                                    Description = subject,
                                    Metadata = new Dictionary<string, string>
                                    {
                                        ["Location"] = location,
                                        ["IsAllDay"] = "True",
                                        ["SpecialEvent"] = "True"
                                    }
                                });

                                Log.Debug("Added special whole-day event: {Subject} ({EventType})", subject, specialEventType);
                                continue; // Skip normal processing
                            }

                            if (isAllDay && !_config.IncludeAllDayEvents)
                                continue;

                            var duration = itemEnd - itemStart;
                            if (duration.TotalMinutes < _config.MinimumDurationMinutes)
                                continue;

                            bool isTeamsMeeting = false;
                            string? meetingUrl = null;

                            try
                            {
                                string body = item.Body ?? string.Empty;
                                isTeamsMeeting = body.Contains("Microsoft Teams", StringComparison.OrdinalIgnoreCase) ||
                                               body.Contains("teams.microsoft.com", StringComparison.OrdinalIgnoreCase);

                                if (isTeamsMeeting && body.Contains("https://teams.microsoft.com"))
                                {
                                    var urlStart = body.IndexOf("https://teams.microsoft.com", StringComparison.Ordinal);
                                    if (urlStart >= 0)
                                    {
                                        var urlEnd = body.IndexOfAny([' ', '\n', '\r', '<'], urlStart);
                                        meetingUrl = urlEnd > urlStart ? body[urlStart..urlEnd] : body[urlStart..];
                                    }
                                }
                            }
                            catch
                            {
                                // Ignore body parsing errors
                            }

                            if (isTeamsMeeting && !_config.IncludeTeamsMeetings)
                                continue;

                            var metadata = new Dictionary<string, string>
                            {
                                ["Location"] = location,
                                ["IsAllDay"] = isAllDay.ToString(),
                                ["IsTeamsMeeting"] = isTeamsMeeting.ToString()
                            };

                            if (!string.IsNullOrWhiteSpace(meetingUrl))
                            {
                                metadata["TeamsMeetingUrl"] = meetingUrl;
                            }

                            events.Add(new Event
                            {
                                Source = Name,
                                Timestamp = itemStart,
                                EventType = "CalendarMeetingStart",
                                Description = subject,
                                Metadata = metadata
                            });

                            events.Add(new Event
                            {
                                Source = Name,
                                Timestamp = itemEnd,
                                EventType = "CalendarMeetingEnd",
                                Description = subject,
                                Metadata = metadata
                            });

                            Log.Debug("Added calendar event: {Subject} ({Start} - {End})", subject, itemStart, itemEnd);
                        }
                        finally
                        {
                            if (item != null)
                                Marshal.ReleaseComObject(item);
                        }
                    }

                    if (restrictedItems != null)
                        Marshal.ReleaseComObject(restrictedItems);
                }
                finally
                {
                    if (items != null)
                        Marshal.ReleaseComObject(items);
                    if (calendarFolder != null)
                        Marshal.ReleaseComObject(calendarFolder);
                    if (ns != null)
                        Marshal.ReleaseComObject(ns);
                    if (outlook != null)
                        Marshal.ReleaseComObject(outlook);
                }

                Log.Information("Retrieved {Count} calendar events from Outlook via COM", events.Count / 2);
            }
            catch (COMException ex)
            {
                Log.Error(ex, "COM error accessing Outlook. Make sure classic Outlook Desktop is running.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrieving Outlook calendar events");
            }

            return events;
        }, cancellationToken);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var outlook = TryGetActiveOutlook();
                if (outlook != null)
                {
                    Marshal.ReleaseComObject(outlook);
                    Log.Information("Classic Outlook Desktop is running and available");
                    return true;
                }

                if (!_config.RequireRunningInstance)
                {
                    var outlookType = Type.GetTypeFromProgID("Outlook.Application");
                    if (outlookType != null)
                    {
                        Log.Information("Classic Outlook Desktop is installed");
                        return true;
                    }
                }

                Log.Debug("Classic Outlook not available");
            }
            catch
            {
                Log.Debug("COM interop check failed");
            }

            return false;
        }, cancellationToken);
    }

    /// <summary>
    /// Check if the event subject indicates a special whole-day event (Home Office, Holiday, or Zeitausgleich)
    /// </summary>
    private bool IsSpecialWholeDayEvent(string subject, out string eventType)
    {
        eventType = string.Empty;

        if (string.IsNullOrWhiteSpace(subject))
            return false;

        // Log configured event names for debugging
        Log.Debug("Checking subject '{Subject}' against: HO='{HO}', Holiday='{Holiday}', ZA='{ZA}'", 
            subject, _homeOfficeEventName, _holidayEventName, _zeitausgleichEventName);

        // Check for Home Office event
        if (subject.Equals(_homeOfficeEventName, StringComparison.OrdinalIgnoreCase))
        {
            eventType = "SpecialWholeDay-Homeoffice";
            Log.Information("Detected Home Office event: '{Subject}'", subject);
            return true;
        }

        // Check for Holiday/Vacation event
        if (subject.Equals(_holidayEventName, StringComparison.OrdinalIgnoreCase))
        {
            eventType = "SpecialWholeDay-Holiday";
            Log.Information("Detected Holiday event: '{Subject}'", subject);
            return true;
        }

        // Check for Zeitausgleich event
        if (subject.Equals(_zeitausgleichEventName, StringComparison.OrdinalIgnoreCase))
        {
            eventType = "SpecialWholeDay-Zeitausgleich";
            Log.Information("Detected Zeitausgleich event: '{Subject}'", subject);
            return true;
        }

        return false;
    }
}
