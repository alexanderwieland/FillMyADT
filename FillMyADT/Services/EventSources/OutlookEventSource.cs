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
    private readonly OutlookEventSourceConfig _config;

    public string Name => "Outlook Calendar";

    public OutlookEventSource(OutlookEventSourceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
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

                    var filter = $"[Start] >= '{startDate:g}' AND [End] <= '{endDate:g}'";
                    var restrictedItems = items.Restrict(filter);

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
}
