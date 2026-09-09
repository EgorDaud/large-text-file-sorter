namespace FileSorter.Benchmarks;

// Samples the managed-heap high-water mark during a run. It excludes native memory, stack
// memory, and the OS file cache because MemoryPlan bounds managed arrays.
internal sealed class PeakMemorySampler : IDisposable
{
    private readonly Timer _timer;
    private long _peakBytes;

    public PeakMemorySampler(int sampleIntervalMilliseconds)
    {
        SampleIntervalMilliseconds = sampleIntervalMilliseconds;
        _peakBytes = GC.GetTotalMemory(false);
        _timer = new Timer(Sample, null, 0, sampleIntervalMilliseconds);
    }

    public int SampleIntervalMilliseconds { get; }

    public long PeakBytes => Volatile.Read(ref _peakBytes);

    public void Dispose() => _timer.Dispose();

    private void Sample(object? state)
    {
        long current = GC.GetTotalMemory(false);
        long previous;
        do
        {
            previous = Volatile.Read(ref _peakBytes);
            if (current <= previous)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _peakBytes, current, previous) != previous);
    }
}
