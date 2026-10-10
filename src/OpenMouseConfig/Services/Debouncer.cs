namespace OpenMouseConfig.Services;

/// <summary>
/// Per-key trailing-edge debounce. Dragging a DPI slider produces dozens of value changes per
/// second; we only want to send the last one once the user pauses.
/// Must be used from the UI thread (continuations resume there).
/// </summary>
public sealed class Debouncer
{
    private readonly Dictionary<string, CancellationTokenSource> _pending = [];

    /// <summary>Runs <paramref name="action"/> after <paramref name="delay"/> unless <see cref="Run"/> is called again with the same key.</summary>
    public void Run(string key, TimeSpan delay, Func<Task> action)
    {
        if (_pending.Remove(key, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _pending[key] = cts;
        _ = RunAsync(key, delay, action, cts);
    }

    public void CancelAll()
    {
        foreach (var cts in _pending.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _pending.Clear();
    }

    private async Task RunAsync(string key, TimeSpan delay, Func<Task> action, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(delay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        // Superseded after the delay elapsed but before this continuation got to run.
        if (cts.IsCancellationRequested)
        {
            return;
        }

        if (_pending.TryGetValue(key, out var current) && current == cts)
        {
            _pending.Remove(key);
            cts.Dispose();
        }

        await action();
    }
}
