using System.Diagnostics;

namespace FileSorter.Merging;

// Bytes written and output-write wait time for one MergeExecutor call. Partition workers
// update it concurrently, so writers use Interlocked and progress readers use Volatile.Read.
internal sealed class MergeProgress
{
    private long _bytesWritten;
    private long _outputWaitStopwatchTicks;

    public long BytesWritten => Volatile.Read(ref _bytesWritten);

    // Write wait time summed across every worker and pass.
    public double OutputWaitSeconds => (double)Volatile.Read(ref _outputWaitStopwatchTicks) / Stopwatch.Frequency;

    public void AddBytesWritten(int count) => Interlocked.Add(ref _bytesWritten, count);

    public void AddOutputWaitTicks(long ticks) => Interlocked.Add(ref _outputWaitStopwatchTicks, ticks);
}
