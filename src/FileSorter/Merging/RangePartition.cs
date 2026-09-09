using System.Globalization;

namespace FileSorter.Merging;

// A single-pass partition: line-aligned run slices, their byte lengths, and disjoint output
// offsets. Run files use normalized LF terminators, so each worker writes exactly the bytes
// it reads and needs no coordination with other output ranges.
internal sealed record RangePartition(
    long[][] RunOffsets,     // [run][worker]; index WorkerCount holds the run length.
    long[]   SliceBytes,     // [worker]
    long[]   OutputOffsets,  // [worker]
    long     TotalBytes)
{
    public int WorkerCount => SliceBytes.Length;

    // Largest slice divided by smallest; 1.0 is perfect byte balance. Equal-key groups
    // larger than a slice cannot be divided without breaking a key range.
    public double Imbalance
    {
        get
        {
            long smallest = long.MaxValue;
            long largest = 0;
            foreach (long bytes in SliceBytes)
            {
                smallest = Math.Min(smallest, bytes);
                largest = Math.Max(largest, bytes);
            }

            return smallest > 0 ? (double)largest / smallest : double.PositiveInfinity;
        }
    }

    // Derives slice lengths and output offsets, validating monotone, anchored offsets and
    // total bytes before workers can drop or duplicate a range.
    public static RangePartition From(long[][] runOffsets, long[] runLengths, int workerCount, long totalBytes)
    {
        // Check each run's endpoints independently; total bytes can hide a misanchored run.
        for (int r = 0; r < runOffsets.Length; r++)
        {
            if (runOffsets[r][0] != 0)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"The range partition is not anchored: run {r}'s first offset is {runOffsets[r][0]}, not 0."));
            }

            if (runOffsets[r][workerCount] != runLengths[r])
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"The range partition does not reach the run's end: run {r}'s last offset is " +
                    $"{runOffsets[r][workerCount]}, but the run is {runLengths[r]} bytes."));
            }
        }

        long[] sliceBytes = new long[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            long slice = 0;
            foreach (long[] offsets in runOffsets)
            {
                long start = offsets[w];
                long end = offsets[w + 1];
                if (end < start)
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"The range partition is not monotone: worker {w}'s slice runs from {start} to {end}."));
                }

                slice += end - start;
            }

            sliceBytes[w] = slice;
        }

        long[] outputOffsets = new long[workerCount];
        long running = 0;
        for (int w = 0; w < workerCount; w++)
        {
            outputOffsets[w] = running;
            running += sliceBytes[w];
        }

        if (running != totalBytes)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"The range partition covers {running} bytes but the runs hold {totalBytes}."));
        }

        return new RangePartition(runOffsets, sliceBytes, outputOffsets, totalBytes);
    }
}
