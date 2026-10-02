namespace FileSorter.Benchmarks;

// Managed heap only, because that is what MemoryPlan bounds; native memory and the file cache are excluded.
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
