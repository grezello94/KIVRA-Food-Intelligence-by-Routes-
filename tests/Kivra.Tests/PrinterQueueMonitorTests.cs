using Kivra.WindowsBridge;
using Xunit;

namespace Kivra.Tests;

public sealed class PrinterQueueMonitorTests
{
    [Fact]
    public async Task Slow_discovery_does_not_block_the_print_loop_or_start_overlapping_discovery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var monitor = new PrinterQueueMonitor(() =>
        {
            Interlocked.Increment(ref calls);
            started.SetResult();
            release.Wait(TimeSpan.FromSeconds(5));
            return new[] { "TSC USB" };
        });
        try
        {
            monitor.RefreshIfDue();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(await monitor.ApplyRefreshAsync().WaitAsync(TimeSpan.FromSeconds(1)));
            for (var i = 0; i < 20; i++) monitor.RefreshIfDue(force: true);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.Set(); }

        await ApplyWhenReady(monitor);
        Assert.Equal(new[] { "TSC USB" }, monitor.KnownQueues);
    }

    [Fact]
    public async Task Healthy_queues_are_cached_and_missing_queues_can_refresh_sooner()
    {
        var clock = new TestClock();
        var calls = 0;
        var monitor = new PrinterQueueMonitor(() =>
        {
            Interlocked.Increment(ref calls);
            return new[] { calls == 1 ? "TSC USB" : "TSC reconnected" };
        }, clock);

        monitor.RefreshIfDue();
        await ApplyWhenReady(monitor);
        clock.Advance(TimeSpan.FromSeconds(10));
        monitor.RefreshIfDue();
        Assert.False(await monitor.ApplyRefreshAsync());
        Assert.Equal(1, Volatile.Read(ref calls));

        monitor.RefreshIfDue(retrySoon: true);
        await ApplyWhenReady(monitor);
        Assert.Equal(new[] { "TSC reconnected" }, monitor.KnownQueues);
        Assert.Equal(2, Volatile.Read(ref calls));

        clock.Advance(TimeSpan.FromSeconds(30));
        monitor.RefreshIfDue();
        await ApplyWhenReady(monitor);
        Assert.Equal(3, Volatile.Read(ref calls));
    }

    static async Task ApplyWhenReady(PrinterQueueMonitor monitor)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!await monitor.ApplyRefreshAsync()) await Task.Delay(1, timeout.Token);
    }

    sealed class TestClock : TimeProvider
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
