namespace FillMyADT.Extensions;

/// <summary>
/// Extension methods for CancellationTokenSource
/// </summary>
public static class CancellationTokenSourceExtensions
{
    /// <summary>
    /// Cancels and disposes the CancellationTokenSource asynchronously
    /// </summary>
    public static async Task CancelAndDisposeAsync(this CancellationTokenSource? cts)
    {
        if (cts == null) return;

        try
        {
            await cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
        finally
        {
            cts.Dispose();
        }
    }
}
