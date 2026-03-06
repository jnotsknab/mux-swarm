namespace MuxSwarm.State;

public class SeshPersistor(Func<Task> persistAction, int intervalSeconds) : IAsyncDisposable
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(intervalSeconds);
    private readonly CancellationTokenSource _cts = new();
    private Task? _backgroundTask;

    public void Start()
    {
        _backgroundTask = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_interval, ct);
            try { await persistAction(); }
            catch { /* log here, dont exit */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_backgroundTask != null)
            await _backgroundTask.ConfigureAwait(false);
    }
}