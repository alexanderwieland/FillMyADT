using Serilog;

namespace FillMyADT.Services;

/// <summary>
/// Handles user notifications and error messages
/// </summary>
public class NotificationService
{
    public event Action<string>? OnError;
    public event Action<string>? OnSuccess;
    public event Action? OnClearMessages;

    public void ShowError(string message)
    {
        Log.Warning("User notification (error): {Message}", message);
        OnError?.Invoke(message);
    }

    public void ShowSuccess(string message)
    {
        Log.Information("User notification (success): {Message}", message);
        OnSuccess?.Invoke(message);
    }

    public void ClearMessages()
    {
        OnClearMessages?.Invoke();
    }
}
