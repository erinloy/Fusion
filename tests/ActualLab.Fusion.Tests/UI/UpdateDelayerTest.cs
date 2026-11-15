using System.Runtime.CompilerServices;
using ActualLab.Fusion.UI;

namespace ActualLab.Fusion.Tests.UI;

public class UpdateDelayerTest(ITestOutputHelper @out) : SimpleFusionTestBase(@out)
{
    // A Blazor circuit disposes its scoped UIActionTracker while its states' delays are pending: the delay wins the race,
    // the tracker's WhenNext() then faults with ObjectDisposedException, and nobody observed it (ziltch prod: ~32 an hour).
    [Fact]
    public async Task ADisposedTrackerLeavesNoUnobservedTaskException()
    {
        var unobserved = 0;
        void Note(object? _, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x is ObjectDisposedException { ObjectName: nameof(UIActionTracker) }))
                Interlocked.Increment(ref unobserved);
        }
        TaskScheduler.UnobservedTaskException += Note;
        try {
            for (var i = 0; i < 8; i++)
                await DelayThenDispose();
            for (var i = 0; i < 3; i++) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            unobserved.Should().Be(0);
        }
        finally {
            TaskScheduler.UnobservedTaskException -= Note;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task DelayThenDispose()
    {
        var services = CreateServices();
        var tracker = new UIActionTracker(new UIActionTracker.Options(), services);
        var delayer = new UpdateDelayer(tracker, TimeSpan.FromMilliseconds(20)) { MinDelay = TimeSpan.Zero };
        await delayer.Delay(0);
        await tracker.DisposeAsync();
    }
}
