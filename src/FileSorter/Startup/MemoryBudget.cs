namespace FileSorter.Startup;

internal static class MemoryBudget
{
    // High fan-in avoids extra merge passes but can require
    // MergeParallelism * MaxMergeFanIn open run handles.
    private const int MaxMergeFanIn = 2048;

    // Extra budget beyond max fan-in grows read-ahead up to the NVMe throughput plateau.
    private const int MaxReadAheadBufferSize = 4 * 1024 * 1024;

    // A fan-in of one cannot reduce the run count.
    private const int MinMergeFanIn = 2;

    // More workers divide the fixed merge budget across smaller read-ahead windows.
    // Cap concurrency before window pressure outweighs partitioning throughput.
    private const int MaxMergeParallelism = 8;

    // The largest budget the arithmetic plans against. Chunk and window sizes saturate far
    // below it, and it keeps budget * assumedMeanLineLength within a long for any mean
    // line length up to 4 KiB (the CLI assumes 32), so a larger --memory cannot wrap.
    private const long MaxPlannedBudgetBytes = 1L << 50;

    // Every FileStream passes this as bufferSize, which disables its internal buffer (D12). Read-ahead
    // and staging buffers are budgeted in the plan, and a second hidden buffer would copy the data twice.
    internal const int UnbufferedStream = 1;

    // KWayMerge uses this as its only output buffer. It keeps concurrent partition
    // writers from spending disproportionate time waiting for output.
    private const int OutputBufferSize = 1024 * 1024;

    // Each phase-one worker gets this spill buffer, so it is budgeted per worker.
    // Larger spill buffers have not improved the smaller concurrent run-file writes.
    private const int SpillBufferSize = 64 * 1024;

    // Throws for a budget that cannot support the other arguments, naming the minimum
    // that can. Use TryCalculate to test viability without an exception.
    public static MemoryPlan Calculate(
        long budgetBytes, int parallelism, int maxLineLength, int assumedMeanLineLength)
    {
        if (TryCalculate(budgetBytes, parallelism, maxLineLength, assumedMeanLineLength, out MemoryPlan plan))
        {
            return plan;
        }

        long minimumBudget = MinimumViableBudget(parallelism, maxLineLength, assumedMeanLineLength);
        throw new ArgumentOutOfRangeException(nameof(budgetBytes), budgetBytes,
            $"A memory budget of {budgetBytes} bytes cannot support a chunk larger than the maximum line " +
            $"length ({maxLineLength} bytes) and a merge fan-in of at least {MinMergeFanIn}, at parallelism " +
            $"{parallelism}. The minimum viable budget is {minimumBudget} bytes.");
    }

    // The single source of the budget algebra. Returns false when the budget cannot
    // support the other arguments; arguments that are invalid whatever the budget still throw.
    internal static bool TryCalculate(
        long budgetBytes, int parallelism, int maxLineLength, int assumedMeanLineLength, out MemoryPlan plan)
    {
        if (parallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parallelism), parallelism,
                "Parallelism must be positive.");
        }

        if (maxLineLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLineLength), maxLineLength,
                "The maximum line length must be positive.");
        }

        if (assumedMeanLineLength <= 0 || assumedMeanLineLength > maxLineLength)
        {
            throw new ArgumentOutOfRangeException(nameof(assumedMeanLineLength), assumedMeanLineLength,
                $"The assumed mean line length must be positive and no greater than the maximum line length ({maxLineLength}).");
        }

        budgetBytes = Math.Min(budgetBytes, MaxPlannedBudgetBytes);

        int descriptorSize = MemoryPlan.DescriptorSize;

        // Reserve maxLineLength + 1 bytes for a carry ending in CR, plus one fresh byte
        // so the next read can find LF or EOF.
        int floorReadAheadBufferSize = AddOffsetChecked(maxLineLength, 2);
        int floorDescriptorCapacity = floorReadAheadBufferSize / assumedMeanLineLength;

        // Each cursor owns two byte windows and descriptors for the delivered window.
        long floorBytesPerRunCursor =
            2L * floorReadAheadBufferSize + (long)floorDescriptorCapacity * descriptorSize;

        // Charge loser-tree storage per cursor, as the merge does at run time.
        long floorBytesPerFanInSlot = floorBytesPerRunCursor + MemoryPlan.LoserTreeBytesPerFanInSlot;

        long fanInRoom = budgetBytes - OutputBufferSize;

        // Test viability at one worker and the minimum window. Window growth and
        // worker selection must not change the minimum accepted budget.
        long rawFanIn = fanInRoom < 0 ? 0 : fanInRoom / floorBytesPerFanInSlot;
        bool phaseTwoViable = rawFanIn >= MinMergeFanIn;

        // Reserve one output buffer per worker and one shared partition-offset table.
        // The table has n + 1 boundaries per run and is absent for sequential merging.
        // WindowFor charges loser-tree storage separately at its assumed fan-in.
        long RoomFor(int n) => budgetBytes
            - (long)n * OutputBufferSize
            - (n > 1 ? (long)MaxMergeFanIn * (n + 1) * MemoryPlan.PartitionOffsetEntrySize : 0);

        long WindowFor(int n)
        {
            long room = RoomFor(n);
            if (room <= 0)
            {
                return 0;
            }

            // At max fan-in, n workers need n * MaxMergeFanIn loser-tree slots.
            // With window size R, mean line length m, and descriptor size D, solve
            // R = adjustedRoom * m / (n * MaxMergeFanIn * (2m + D)).
            // Integer rounding leaves room for the actual descriptor arrays.
            long adjustedRoom = room - (long)n * MaxMergeFanIn * MemoryPlan.LoserTreeBytesPerFanInSlot;
            if (adjustedRoom <= 0)
            {
                return 0;
            }

            long denominator = (long)n * MaxMergeFanIn * (2L * assumedMeanLineLength + descriptorSize);
            return adjustedRoom * assumedMeanLineLength / denominator;
        }

        // Choose the largest worker count that still affords max fan-in at the
        // minimum window. Extra workers must not force more merge passes.
        int mergeParallelism = 1;
        for (int n = Math.Min(parallelism, MaxMergeParallelism); n > 1; n--)
        {
            if (WindowFor(n) >= floorReadAheadBufferSize)
            {
                mergeParallelism = n;
                break;
            }
        }

        // Loser-tree cost is charged below at the actual fan-in.
        long mergeRoom = RoomFor(mergeParallelism);

        // Keep each window at or above the progress floor and cap growth at the measured
        // throughput plateau. The progress floor takes precedence when it is larger.
        long rawWindow = mergeRoom <= 0 ? floorReadAheadBufferSize : WindowFor(mergeParallelism);
        long window = Math.Max(floorReadAheadBufferSize, Math.Min(rawWindow, MaxReadAheadBufferSize));

        long BytesPerRunCursor(long readAhead) =>
            2L * readAhead + (readAhead / assumedMeanLineLength) * descriptorSize;

        int readAheadBufferSize = (int)window;
        int readAheadDescriptorCapacity = (int)(window / assumedMeanLineLength);

        // Every worker opens the same runs. Charge cursor and loser-tree storage
        // for all workers at the fan-in selected below.
        long bytesPerRunCursor = BytesPerRunCursor(window);
        long cursorAndMetadataCostAcrossWorkers = (long)mergeParallelism * (bytesPerRunCursor + MemoryPlan.LoserTreeBytesPerFanInSlot);

        // A viable floor window permits at least MinMergeFanIn; a grown window
        // was sized to afford MaxMergeFanIn.
        int mergeFanIn = mergeRoom < cursorAndMetadataCostAcrossWorkers
            ? 0
            : (int)Math.Min(MaxMergeFanIn, mergeRoom / cursorAndMetadataCostAcrossWorkers);

        // Keep requested parallelism fixed; adding workers as the budget grows would
        // create sudden drops in chunk size. Reserve each worker's spill buffer first.
        // For chunk size C, mean line length m, descriptor size D, and p workers:
        // phase-one cost <= (p + 2) * C * (1 + D/m) + C + p * SpillBufferSize.
        // The extra C holds displaced prefetched bytes; p + 2 matches PoolCapacity.
        long spillRoom = budgetBytes - (long)parallelism * SpillBufferSize;
        long denominator = (long)(parallelism + 2) * (assumedMeanLineLength + descriptorSize) + assumedMeanLineLength;
        long rawChunkSize = spillRoom <= 0 ? 0 : spillRoom * assumedMeanLineLength / denominator;

        // The chunk is one byte[] in BufferPool and ChunkReader, and the largest
        // allocatable array is Array.MaxLength, a little below int.MaxValue.
        int chosenChunkSize = rawChunkSize > Array.MaxLength ? Array.MaxLength : (int)rawChunkSize;

        // ChunkReader reserves maxLineLength + 2 bytes before prefetching, so the
        // chunk must hold at least one more byte. Compare in long to avoid overflow
        // for direct callers near int.MaxValue.
        if ((long)chosenChunkSize <= (long)maxLineLength + 2 || !phaseTwoViable)
        {
            plan = default;
            return false;
        }

        int descriptorCapacity = chosenChunkSize / assumedMeanLineLength;

        plan = new(
            ChunkSize: chosenChunkSize,
            DescriptorCapacity: descriptorCapacity,
            Parallelism: parallelism,
            MergeFanIn: mergeFanIn,
            ReadAheadBufferSize: readAheadBufferSize,
            ReadAheadDescriptorCapacity: readAheadDescriptorCapacity,
            OutputBufferSize: OutputBufferSize,
            SpillBufferSize: SpillBufferSize,
            MergeParallelism: mergeParallelism);

        return true;
    }

    // The smallest budget for which TryCalculate succeeds, found by bisection. Viability
    // is monotone in the budget (property tests PB-21 and PB-23), which is what makes
    // bisection valid. The low bound is the output buffer: any budget up to it leaves no
    // room for a merge cursor, so it is surely unviable. The high bound is the largest
    // budget TryCalculate plans against (2^50 bytes), so a larger one cannot be viable
    // where this is not. It exceeds the budget needed at the CLI's --max-line ceiling
    // (Array.MaxLength - 3) up to a parallelism of about 250,000. Where nothing is viable
    // (a line limit above that ceiling), the search ends at the high bound, so the result
    // is always positive.
    internal static long MinimumViableBudget(int parallelism, int maxLineLength, int assumedMeanLineLength)
    {
        long unviable = OutputBufferSize;
        long viable = MaxPlannedBudgetBytes;

        while (viable - unviable > 1)
        {
            long middle = unviable + (viable - unviable) / 2;
            if (TryCalculate(middle, parallelism, maxLineLength, assumedMeanLineLength, out _))
            {
                viable = middle;
            }
            else
            {
                unviable = middle;
            }
        }

        return viable;
    }

    // Check the addition before narrowing so direct callers cannot get a wrapped
    // negative buffer size, even outside the CLI's limits.
    private static int AddOffsetChecked(int maxLineLength, int offset)
    {
        long result = (long)maxLineLength + offset;
        if (result > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLineLength), maxLineLength,
                $"The maximum line length is too large: adding this method's own buffer-floor offset ({offset}) would not fit in a 32-bit size.");
        }

        return (int)result;
    }
}
