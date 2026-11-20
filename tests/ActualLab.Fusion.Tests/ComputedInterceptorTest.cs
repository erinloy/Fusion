using System.Runtime.CompilerServices;
using ActualLab.Fusion.Tests.Services;

namespace ActualLab.Fusion.Tests;

public class ComputedInterceptorTest(ITestOutputHelper @out) : FusionTestBase(@out)
{
    protected override void ConfigureTestServices(IServiceCollection services, bool isClient)
    {
        base.ConfigureTestServices(services, isClient);
        var fusion = services.AddFusion();
        if (!isClient)
            fusion.AddService<ITimeService, TimeService>();
    }

    [Fact]
    public async Task AutoRecomputeTest()
    {
        var stateFactory = Services.StateFactory();
        var time = Services.GetRequiredService<ITimeService>();
        var c = await Computed.Capture(() => time.GetTimeWithOffset(TimeSpan.FromSeconds(1)));

        var count = 0L;
        using var state = stateFactory.NewComputed<DateTime>(
            FixedDelayer.YieldUnsafe,
            async ct => await c.Use(ct));
        state.Updated += (s, _)
            => Log?.LogInformation($"{++count} -> {s.Value:hh:mm:ss:fff}");

        await TestExt.When(
            () => count.Should().BeGreaterThan(2),
            TimeSpan.FromSeconds(5));
        var lastCount = count;
        state.Dispose();

        await Task.Delay(1000);
        count.Should().Be(lastCount);
    }

    [Fact]
    public async Task CancellationTest()
    {
        var time = Services.GetRequiredService<ITimeService>();

        for (var i = 0; i < 5; i++) {
            await Task.Delay(300);
            var cts = new CancellationTokenSource();
            var task = time.GetTimeWithDelay(cts.Token);
            cts.Cancel();
            try {
                await task.ConfigureAwait(false);
                Assert.Fail("OperationCanceledException wasn't thrown.");
            }
            catch (OperationCanceledException) {
                // Intended
            }

            task = time.GetTimeWithDelay(default);
            await TestExt.When(
                () => task.IsCompletedSuccessfully.Should().BeTrue(),
                TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task InvalidationAndCachingTest1()
    {
        var time = Services.GetRequiredService<ITimeService>();

        var c1 = await Computed.Capture(() => time.GetTime());

        // Wait for time invalidation
        await Task.Delay(500);

        var c2a = await Computed.Capture(() => time.GetTime());
        c2a.Should().NotBeSameAs(c1);
        var c2b = await Computed.Capture(() => time.GetTime());
        c2b.Should().BeSameAs(c2a);
    }

    [Fact]
    public async Task InvalidationAndCachingTest2()
    {
        // TODO: Fix the test so that it starts right after the time invalidation,
        // otherwise it has a tiny chance of failure
        var time = Services.GetRequiredService<ITimeService>();

        var c1 = await Computed.Capture(() => time.GetTimeWithOffset(TimeSpan.FromSeconds(1)));
        c1.Should().NotBeNull();
        var c2 = await Computed.Capture(() => time.GetTimeWithOffset(TimeSpan.FromSeconds(2)));
        c2.Should().NotBeNull();
        c1.Should().NotBeSameAs(c2);

        var c1a = await Computed.Capture(() => time.GetTimeWithOffset(TimeSpan.FromSeconds(1)));
        var c2a = await Computed.Capture(() => time.GetTimeWithOffset(TimeSpan.FromSeconds(2)));
        c1.Should().BeSameAs(c1a);
        c2.Should().BeSameAs(c2a);

        // Wait for time invalidation
        await Task.Delay(500);

        c1a = await Computed.Capture(() => time.GetTimeWithOffset(TimeSpan.FromSeconds(1)));
        c2a = await Computed.Capture(() => time.GetTimeWithOffset(TimeSpan.FromSeconds(2)));
        c1.Should().NotBeSameAs(c1a);
        c2.Should().NotBeSameAs(c2a);
    }

    [Fact]
    public async Task InvalidationReturnsCompletedDefaultsForEveryAsyncShape()
    {
        var time = Services.GetRequiredService<ITimeService>();
        var taskComputed = await Computed.Capture(() => time.GetTime());
        var valueTaskComputed = await Computed.Capture(() => time.GetTimeAsValueTask());
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        using (Invalidation.Begin()) {
            var task = time.GetTime(cancellationSource.Token);
            task.IsCompletedSuccessfully.Should().BeTrue();
            task.Result.Should().Be(default);

            var valueTask = time.GetTimeAsValueTask(cancellationSource.Token);
            valueTask.IsCompletedSuccessfully.Should().BeTrue();
            valueTask.Result.Should().Be(default);
        }

        await taskComputed.WhenInvalidated().WaitAsync(TimeSpan.FromSeconds(1));
        await valueTaskComputed.WhenInvalidated().WaitAsync(TimeSpan.FromSeconds(1));
    }

    // ziltch prod 0000300 (11-20 12:18Z): a page GL block's run was cancelled at its budget after its caller stopped waiting
    // (WaitAsync timed out). The value promise was a ContinueWith whose GetResult rethrew the cancel, so it faulted with
    // "A task was canceled", nobody read it, and the finalizer raised it as an unobserved task exception.
    [Fact]
    public async Task ACancelledComputeAbandonedByItsCallerLeavesNoUnobservedTaskException()
    {
        var time = Services.GetRequiredService<ITimeService>();
        var unobserved = 0;
        var first = "";
        void Note(object? _, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x is OperationCanceledException)
                && Interlocked.Increment(ref unobserved) == 1)
                first = e.Exception.ToString();
        }
        TaskScheduler.UnobservedTaskException += Note;
        try {
            for (var i = 0; i < 8; i++)
                await AskThenAbandon(time);
            for (var i = 0; i < 3; i++) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally {
            TaskScheduler.UnobservedTaskException -= Note;
        }
        unobserved.Should().Be(0, first);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AskThenAbandon(ITimeService time)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var asked = time.GetTimeWithDelay(cts.Token);
        // the caller stops waiting first, as a WaitAsync past its budget does, and never reads the task again
        try { await asked.WaitAsync(TimeSpan.FromMilliseconds(5)); } catch (TimeoutException) { } catch (OperationCanceledException) { }
        await Task.Delay(300);
        asked.IsCompleted.Should().BeTrue();
    }
}
