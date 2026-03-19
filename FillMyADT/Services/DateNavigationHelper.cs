namespace FillMyADT.Services;

/// <summary>
/// Helper for date navigation and calculations
/// </summary>
public static class DateNavigationHelper
{
    public static DateTime Today => DateTime.Today;
    public static DateTime Yesterday => DateTime.Today.AddDays(-1);
    
    public static DateTime GetPreviousDay(DateTime current) => current.AddDays(-1);
    public static DateTime GetNextDay(DateTime current) => current.AddDays(1);
    
    public static bool CanNavigateToNextDay(DateTime current) => current < DateTime.Today;
    
    public static DateTime ClampToToday(DateTime date) => date > DateTime.Today ? DateTime.Today : date;
}
