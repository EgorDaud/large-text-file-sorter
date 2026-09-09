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

    // KWayMerge uses this as its only output buffer. It keeps concurrent partition
    // writers from spending disproportionate time waiting for output.
    private const int OutputBufferSize = 1024 * 1024;

    // Each phase-one worker gets this spill buffer, so it is budgeted per worker.
    // Larger spill buffers have not improved the smaller concurrent run-file writes.
    private const int SpillBufferSize = 64 * 1024;

    public static MemoryPlan Calculate(
        long budgetBytes, int parallelism, int maxLineLength, int assumedMeanLineLength)
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

        int descriptorSize = MemoryPlan.DescriptorSize;

        // Reserve maxLineLength + 1 bytes for a carry ending in CR, plus one fresh byte
        // so the next read can find LF or EOF.
        int floorReadAheadBufferSize = AddOffsetChecked(maxLineLength, 2);
        int floorDescriptorCapacity = floorReadAheadBufferSize / assumedMeanLineLength;

        // Each cursor owns two byte windows and descriptors for the delivered window.
        long floorBytesPerRunCursor =
            2L * floorReadAheadBufferSize + (long)floorDescriptorCapacity * descriptorSize;

        // Charge loser-tree storage per cursor so this boundary matches the reference
        // plan used by MinimumViableBudget.
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

        // floor(R/m)*D <= (R/m)*D makes the continuous solution conservative.
        // Check the discrete cost here to guard future changes to the formula.
        while (window > floorReadAheadBufferSize
               && (long)mergeParallelism * MaxMergeFanIn * (BytesPerRunCursor(window) + MemoryPlan.LoserTreeBytesPerFanInSlot) > mergeRoom)
        {
            window--;
        }

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
        int chosenChunkSize = rawChunkSize > int.MaxValue ? int.MaxValue : (int)rawChunkSize;

        // ChunkReader reserves maxLineLength + 2 bytes before prefetching, so the
        // chunk must hold at least one more byte. Compare in long to avoid overflow
        // for direct callers near int.MaxValue.
        if ((long)chosenChunkSize <= (long)maxLineLength + 2 || !phaseTwoViable)
        {
            long minimumBudget = MinimumViableBudget(parallelism, maxLineLength, assumedMeanLineLength);
            throw new ArgumentOutOfRangeException(nameof(budgetBytes), budgetBytes,
                $"A memory budget of {budgetBytes} bytes cannot support a chunk larger than the maximum line " +
                $"length ({maxLineLength} bytes) and a merge fan-in of at least {MinMergeFanIn}, at parallelism " +
                $"{parallelism}. The minimum viable budget is {minimumBudget} bytes.");
        }

        int descriptorCapacity = chosenChunkSize / assumedMeanLineLength;

        MemoryPlan plan = new(
            ChunkSize: chosenChunkSize,
            DescriptorCapacity: descriptorCapacity,
            Parallelism: parallelism,
            MergeFanIn: mergeFanIn,
            ReadAheadBufferSize: readAheadBufferSize,
            ReadAheadDescriptorCapacity: readAheadDescriptorCapacity,
            OutputBufferSize: OutputBufferSize,
            SpillBufferSize: SpillBufferSize,
            MergeParallelism: mergeParallelism);

        // Check the constructed plan independently of the solve. Flooring the chunk
        // and descriptor counts should make this loop unnecessary.
        while (plan.WorstCasePhaseOneBytes > budgetBytes && (long)plan.ChunkSize > (long)maxLineLength + 3)
        {
            int shrunk = plan.ChunkSize - 1;
            plan = plan with { ChunkSize = shrunk, DescriptorCapacity = shrunk / assumedMeanLineLength };
        }

        // Recheck phase two through MemoryPlan so formula changes cannot silently
        // exceed the budget or reduce fan-in below the planner's minimum.
        while (plan.WorstCasePhaseTwoBytes > budgetBytes && plan.MergeFanIn > MinMergeFanIn)
        {
            plan = plan with { MergeFanIn = plan.MergeFanIn - 1 };
        }

        // Reject an invalid plan if shrinking to the minimum fan-in was insufficient.
        if (plan.WorstCasePhaseTwoBytes > budgetBytes)
        {
            throw new InvalidOperationException(
                $"Internal invariant violated: the constructed plan's phase-two worst case " +
                $"({plan.WorstCasePhaseTwoBytes} bytes) exceeds the budget ({budgetBytes} bytes).");
        }

        if (plan.MergeFanIn < MinMergeFanIn)
        {
            throw new InvalidOperationException(
                $"Internal invariant violated: the constructed plan's merge fan-in ({plan.MergeFanIn}) is " +
                $"below the minimum MergePlanner accepts ({MinMergeFanIn}).");
        }

        return plan;
    }

    // Shared by Calculate and CLI diagnostics to keep the reported minimum exact.
    internal static long MinimumViableBudget(int parallelism, int maxLineLength, int assumedMeanLineLength)
    {
        int outputBufferSize = OutputBufferSize;

        // Use the requested phase-one parallelism and minimum viable buffers.
        // ChunkReader needs maxLineLength + 2 reserved bytes plus one fresh byte;
        // RunCursor folds known carry in place and needs only maxLineLength + 2 total.
        int chunkSize = AddOffsetChecked(maxLineLength, 3);
        int descriptorCapacity = chunkSize / assumedMeanLineLength;
        int readAheadBufferSize = AddOffsetChecked(maxLineLength, 2);
        int readAheadDescriptorCapacity = readAheadBufferSize / assumedMeanLineLength;

        MemoryPlan reference = new(
            ChunkSize: chunkSize,
            DescriptorCapacity: descriptorCapacity,
            Parallelism: parallelism,
            MergeFanIn: MinMergeFanIn,
            ReadAheadBufferSize: readAheadBufferSize,
            ReadAheadDescriptorCapacity: readAheadDescriptorCapacity,
            OutputBufferSize: outputBufferSize,
            SpillBufferSize: SpillBufferSize,
            // Match Calculate's viability test, which uses one worker at the floor window.
            MergeParallelism: 1);

        // Phase two's reference cost is exact. Phase one needs the inverse of the
        // continuous solve: paying for the discrete reference plan can still produce
        // a floored chunk size one byte too small.
        long phaseOneMinimum = PhaseOneMinimumFor(parallelism, chunkSize, assumedMeanLineLength);

        return Math.Max(phaseOneMinimum, reference.WorstCasePhaseTwoBytes);
    }

    // Invert Calculate's phase-one solve to find the smallest budget that yields
    // requiredChunkSize. Keep this equation aligned with Calculate.
    private static long PhaseOneMinimumFor(int parallelism, int requiredChunkSize, int assumedMeanLineLength)
    {
        long denominator = (long)(parallelism + 2) * (assumedMeanLineLength + MemoryPlan.DescriptorSize) + assumedMeanLineLength;
        long numerator = (long)requiredChunkSize * denominator;

        // Round up so Calculate's floored chunk size reaches requiredChunkSize.
        long budgetAboveSpillBuffers = (numerator + assumedMeanLineLength - 1) / assumedMeanLineLength;

        return (long)parallelism * SpillBufferSize + budgetAboveSpillBuffers;
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
