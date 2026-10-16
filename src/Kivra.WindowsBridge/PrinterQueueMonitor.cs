namespace Kivra.WindowsBridge;

// The bridge loop owns this monitor. Discovery runs on a worker thread, and the
// loop only consumes completed results so a slow spooler cannot delay a label.
internal sealed class PrinterQueueMonitor(Func<IReadOnlyList<string>> enumerateQueues, TimeProvider? timeProvider = null)
{
    readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    Task<IReadOnlyList<string>>? refresh;
    DateTimeOffset lastRefresh = DateTimeOffset.MinValue;

    public IReadOnlyList<string>? KnownQueues { get; private set; }

    public void RefreshIfDue(bool retrySoon = false, bool force = false)
    {
        var interval = TimeSpan.FromSeconds(retrySoon ? 5 : 30);
        if (refresh is not null || (!force && clock.GetUtcNow() - lastRefresh < interval)) return;
        lastRefresh = clock.GetUtcNow();
        refresh = Task.Run(enumerateQueues);
    }

    public async Task<bool> ApplyRefreshAsync()
    {
        if (refresh is not { IsCompleted: true }) return false;
        var completed = refresh;
        refresh = null;
        KnownQueues = await completed;
        return true;
    }
}
