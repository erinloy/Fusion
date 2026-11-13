using ActualLab.Internal;

namespace ActualLab.Collections;

/// <summary>
/// A radix heap with set semantics, providing efficient monotone priority queue operations
/// using integer priorities. Each value can appear at most once.
/// </summary>
public class RadixHeapSet<T> : IEnumerable<(long Priority, T Value)>
    where T : notnull
{
    private const long UnknownMin = long.MinValue;
    private static readonly Option<(long Priority, T Value)> None = Option<(long Priority, T Value)>.None;
    private static readonly Dictionary<T, long> Empty = new();
    private readonly Dictionary<T, int> _bucketIndexes;
    private readonly Dictionary<T, long>[] _buckets;
    // Each bucket's min priority (long.MaxValue when empty), or UnknownMin until it's re-scanned
    private readonly long[] _bucketMins;

    public int Count {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _bucketIndexes.Count;
    }
    public long MinPriority { get; private set; }

    public RadixHeapSet(int bucketCount = 65, long minPriority = 0)
    {
        MinPriority = minPriority;
        _bucketIndexes = new Dictionary<T, int>();
        _buckets = new Dictionary<T, long>[bucketCount];
        _bucketMins = new long[bucketCount];
        for (var i = 0; i < _buckets.Length; i++) {
            _buckets[i] = new Dictionary<T, long>();
            _bucketMins[i] = long.MaxValue;
        }
    }

    public RadixHeapSet(RadixHeapSet<T> other)
    {
        MinPriority = other.MinPriority;
        _bucketIndexes = new Dictionary<T, int>(other._bucketIndexes);
        _buckets = new Dictionary<T, long>[other._buckets.Length];
        _bucketMins = (long[])other._bucketMins.Clone();
        for (var i = 0; i < _buckets.Length; i++)
            _buckets[i] = new Dictionary<T, long>(other._buckets[i]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public IEnumerator<(long Priority, T Value)> GetEnumerator()
    {
        var copy = new RadixHeapSet<T>(this);
        while (true) {
            var min = copy.ExtractMin();
            if (min.IsSome(out var result))
                yield return result;
            else
                break;
        }
    }

#if NET5_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public bool Add(long priority, T value)
    {
        var index = GetBucketIndex(priority);
        if (!_bucketIndexes.TryAdd(value, index))
            return false;

        _buckets[index].Add(value, priority);
        OnAdded(index, priority);
        return true;
    }

#if NET5_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public void AddOrUpdate(long priority, T value)
    {
        var index = GetBucketIndex(priority);
        var bucket = _buckets[index];
        if (_bucketIndexes.TryGetValue(value, out var oldIndex)) {
            if (oldIndex == index) {
                OnRemoved(index, bucket[value]);
                bucket[value] = priority;
                OnAdded(index, priority);
                return;
            }
            _buckets[oldIndex].Remove(value, out var oldPriority);
            OnRemoved(oldIndex, oldPriority);
        }
        _bucketIndexes[value] = index;
        bucket.Add(value, priority);
        OnAdded(index, priority);
    }

#if NET5_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public bool AddOrUpdateToLower(long priority, T value)
    {
        var index = GetBucketIndex(priority);
        var bucket = _buckets[index];
        if (_bucketIndexes.TryGetValue(value, out var oldIndex)) {
            if (oldIndex == index) {
                var oldPriority = bucket[value];
                if (oldPriority <= priority)
                    return false;

                bucket[value] = priority;
                OnAdded(index, priority);
                return true;
            }
            else {
                var oldBucket = _buckets[oldIndex];
                var oldPriority = oldBucket[value];
                if (oldPriority <= priority)
                    return false;

                oldBucket.Remove(value);
                OnRemoved(oldIndex, oldPriority);
            }
        }
        _bucketIndexes[value] = index;
        bucket.Add(value, priority);
        OnAdded(index, priority);
        return true;
    }

#if NET5_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public bool AddOrUpdateToHigher(long priority, T value)
    {
        var index = GetBucketIndex(priority);
        var bucket = _buckets[index];
        if (_bucketIndexes.TryGetValue(value, out var oldIndex)) {
            if (oldIndex == index) {
                var oldPriority = bucket[value];
                if (oldPriority >= priority)
                    return false;

                bucket[value] = priority;
                OnRemoved(index, oldPriority);
                return true;
            }
            else {
                var oldBucket = _buckets[oldIndex];
                var oldPriority = oldBucket[value];
                if (oldPriority >= priority)
                    return false;

                oldBucket.Remove(value);
                OnRemoved(oldIndex, oldPriority);
            }
        }
        _bucketIndexes[value] = index;
        bucket.Add(value, priority);
        OnAdded(index, priority);
        return true;
    }

#if NET5_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public bool Remove(T value, out long priority)
    {
        if (!_bucketIndexes.Remove(value, out var index)) {
            priority = default;
            return false;
        }
        _buckets[index].Remove(value, out priority);
        OnRemoved(index, priority);
        return true;
    }

    public Option<(long Priority, T Value)> PeekMin()
    {
        UpdateMinPriority();
        if (Count == 0)
            return None;
        var bucket = _buckets[0];
        var result = bucket.First();
        return Option.Some((result.Value, result.Key));
    }

    public IReadOnlyDictionary<T, long> PeekMinSet()
    {
        UpdateMinPriority();
        return _buckets[0];
    }

    public Option<(long Priority, T Value)> ExtractMin()
    {
        UpdateMinPriority();
        if (Count == 0)
            return None;
        var bucket = _buckets[0];
        var result = bucket.First();
        bucket.Remove(result.Key);
        _bucketIndexes.Remove(result.Key);
        if (bucket.Count == 0)
            _bucketMins[0] = long.MaxValue;
        return Option.Some((result.Value, result.Key));
    }

    public IReadOnlyDictionary<T, long> ExtractMinSet()
    {
        UpdateMinPriority();
        return ExtractBucket0();
    }

    public IReadOnlyDictionary<T, long> ExtractMinSet(long priority)
    {
        if (priority < MinPriority || Count == 0)
            return Empty;
        if (priority == MinPriority)
            return ExtractBucket0();

        for (var index = 0; index < _buckets.Length; index++) {
            var bucket = _buckets[index];
            if (bucket.Count != 0) {
                if (index == 0)
                    throw new ArgumentOutOfRangeException(nameof(priority));
                var minPriority = GetBucketMin(index);
                if (priority > minPriority)
                    throw new ArgumentOutOfRangeException(nameof(priority));
                // Nothing is due yet: leaving MinPriority behind keeps every entry where it is,
                // so a tick with nothing due copies nothing
                if (priority < minPriority)
                    return Empty;

                MinPriority = priority;
                Redistribute(index);
                return ExtractBucket0();
            }
        }
        throw Errors.InternalError(
            $"{GetType().GetName()}: internal structure is corrupted.");
    }

    // Private methods

    private void UpdateMinPriority()
    {
        if (Count == 0)
            return;

        for (var index = 0; index < _buckets.Length; index++) {
            var bucket = _buckets[index];
            if (bucket.Count != 0) {
                if (index == 0)
                    // No need for update
                    return;
                MinPriority = GetBucketMin(index);
                Redistribute(index);
                return;
            }
        }
        throw Errors.InternalError(
            $"{GetType().GetName()}: internal structure is corrupted.");
    }

    // Requires: buckets [0, index) are empty, and MinPriority <= the min priority of bucket[index]
    private void Redistribute(int index)
    {
        var bucket = _buckets[index];
        _buckets[index] = new Dictionary<T, long>();
        for (var i = 0; i <= index; i++)
            _bucketMins[i] = long.MaxValue;
        foreach (var (value, p) in bucket) {
            var i = GetBucketIndexUnchecked(p);
            _bucketIndexes[value] = i;
            _buckets[i].Add(value, p);
            if (p < _bucketMins[i])
                _bucketMins[i] = p;
        }
    }

    private IReadOnlyDictionary<T, long> ExtractBucket0()
    {
        var bucket = _buckets[0];
        if (bucket.Count != 0) {
            _buckets[0] = new Dictionary<T, long>();
            foreach (var pair in bucket)
                _bucketIndexes.Remove(pair.Key);
        }
        _bucketMins[0] = long.MaxValue;
        return bucket;
    }

    private long GetBucketMin(int index)
    {
        var min = _bucketMins[index];
        if (min != UnknownMin)
            return min;

        min = long.MaxValue;
        foreach (var (_, p) in _buckets[index])
            if (p < min) min = p;
        return _bucketMins[index] = min;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnAdded(int index, long priority)
    {
        var min = _bucketMins[index];
        if (min != UnknownMin && priority < min)
            _bucketMins[index] = priority;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnRemoved(int index, long priority)
    {
        if (_buckets[index].Count == 0)
            _bucketMins[index] = long.MaxValue;
        else if (_bucketMins[index] == priority)
            _bucketMins[index] = UnknownMin;
    }

#if NET5_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    private int GetBucketIndex(long priority)
    {
        if (priority < MinPriority)
            throw new ArgumentOutOfRangeException(nameof(priority));

        priority ^= MinPriority;
#if NET8_0_OR_GREATER
        return 64 - unchecked((int)ulong.LeadingZeroCount((ulong)priority));
#else
        return priority == 0 ? 0 : 1 + Bits.LeadingBitIndex((ulong)priority);
#endif
    }

    private int GetBucketIndexUnchecked(long priority)
    {
        var xor = MinPriority ^ priority;
        return xor == 0 ? 0 : 1 + Bits.LeadingBitIndex((ulong)xor);
    }
}
