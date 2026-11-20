using ActualLab.Caching;

namespace ActualLab.Fusion;

/// <summary>
/// Extension methods for <see cref="IComputeFunction"/>.
/// </summary>
public static class ComputeFunctionExt
{
    private static readonly Type FactoryType1 = typeof(CompleteProduceValuePromiseFactory<>);
    private static readonly Type FactoryType2 = typeof(CompleteProduceValuePromiseWithSynchronizerFactory<>);

    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "We assume ComputeFunctionExt<T> methods are preserved")]
    public static Task ProduceValuePromise(
        this IComputeFunction function,
        ComputedInput input,
        ComputeContext context,
        CancellationToken cancellationToken = default)
    {
        var task = function.ProduceComputed(input, context, cancellationToken);
        if (task.IsCompletedSuccessfully) {
            var computed = task.GetAwaiter().GetResult();
            return computed.GetValuePromise(); // Happy path
        }

        return GenericInstanceCache
            .GetUnsafe<Func<Task<Computed>, Task>>(FactoryType1, function.OutputType)
            .Invoke(task);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "We assume ComputeFunctionExt<T> methods are preserved")]
    public static Task ProduceValuePromise(
        this IComputeFunction function,
        ComputedInput input,
        ComputeContext context,
        ComputedSynchronizer computedSynchronizer,
        CancellationToken cancellationToken = default)
    {
        var task = function.ProduceComputed(input, context, cancellationToken);
        if (task.IsCompletedSuccessfully) {
            var computed = task.GetAwaiter().GetResult();
            if (computedSynchronizer.IsSynchronized(computed))
                return computed.GetValuePromise(); // Happy path
        }

        return GenericInstanceCache
            .GetUnsafe<Func<Task<Computed>, ComputedSynchronizer, CancellationToken, Task>>(FactoryType2, function.OutputType)
            .Invoke(task, computedSynchronizer, cancellationToken);
    }

    // Nested types

    /// <summary>
    /// Factory that creates a delegate to extract a typed value from a <see cref="Computed"/> task.
    /// </summary>
    public sealed class CompleteProduceValuePromiseFactory<T> : GenericInstanceFactory, IGenericInstanceFactory<T>
    {
        [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "We assume Task<T> methods are preserved")]
        public override object Generate()
            => static (Task<Computed> computedTask) => ValueOf(computedTask);

        // An await, not a ContinueWith: a cancelled compute leaves this Canceled, not Faulted with "A task was canceled",
        // so a caller that stopped waiting (it timed out) leaves no unobserved task exception for the finalizer.
        internal static async Task<T> ValueOf(Task<Computed> computedTask)
        {
            var computed = await computedTask.ConfigureAwait(false);
            return (T)computed.UntypedOutput.Value!;
        }
    }

    /// <summary>
    /// Factory that creates a delegate to extract a typed value from a <see cref="Computed"/> task
    /// with <see cref="ComputedSynchronizer"/> support.
    /// </summary>
    public sealed class CompleteProduceValuePromiseWithSynchronizerFactory<T> : GenericInstanceFactory, IGenericInstanceFactory<T>
    {
        [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "We assume Task<T> methods are preserved")]
        public override object Generate()
            => static (Task<Computed> computedTask, ComputedSynchronizer computedSynchronizer, CancellationToken cancellationToken)
                => CompleteProduceValuePromiseFactory<T>.ValueOf(computedSynchronizer.Synchronize(computedTask, cancellationToken));
    }
}
