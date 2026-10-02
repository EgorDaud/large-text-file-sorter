using System.Diagnostics;

namespace FileSorter.Merging;

// Partition workers update this concurrently while a progress reporter reads it.
internal sealed class MergeProgress
{
    private long _bytesWritten;
    private long _outputWaitStopwatchTicks;

    public long BytesWritten => Volatile.Read(ref _bytesWritten);

    public double OutputWaitSeconds => (double)Volatile.Read(ref _outputWaitStopwatchTicks) / Stopwatch.Frequency;

    public void AddBytesWritten(int count) => Interlocked.Add(ref _bytesWritten, count);

    public void AddOutputWaitTicks(long ticks) => Interlocked.Add(ref _outputWaitStopwatchTicks, ticks);
}
