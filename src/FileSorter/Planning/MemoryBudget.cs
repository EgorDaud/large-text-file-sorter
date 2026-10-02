using FileSorter.LineFormat;

namespace FileSorter.Planning;

internal static class MemoryBudget
{
    // Sizes descriptor arrays only; 32 keeps byte buffers filling first on the generated data.
    internal const int AssumedMeanLineLength = 32;

    // Up to MergeParallelism * MaxMergeFanIn run handles can be open at once.
    private const int MaxMergeFanIn = 2048;

    // Measured NVMe read throughput plateau.
    private const int MaxReadAheadBufferSize = 4 * 1024 * 1024;

    private const int MinMergeFanIn = 2;

    private const int MaxMergeParallelism = 8;

    // Keeps budget * assumedMeanLineLength within a long for mean line lengths up to 4 KiB.
    private const long MaxPlannedBudgetBytes = 1L << 50;

    private const int OutputBufferSize = 1024 * 1024;

    private const int SpillBufferSize = 64 * 1024;

    public static MemoryPlan Calculate(
        long budgetBytes, int parallelism, int maxLineLength, int assumedMeanLineLength)
    {
        if (TryCalculate(budgetBytes, parallelism, maxLineLength, assumedMeanLineLength, out MemoryPlan plan))
        {
            return plan;
        }

        string minimum = TryFindMinimumViableBudget(parallelism, maxLineLength, assumedMeanLineLength, out long minimumBudget)
            ? $"The minimum viable budget is {minimumBudget} bytes."
            : "No budget is viable for this line length and parallelism.";
        throw new ArgumentOutOfRangeException(nameof(budgetBytes), budgetBytes,
            $"A memory budget of {budgetBytes} bytes cannot support a chunk larger than the maximum line " +
            $"length ({maxLineLength} bytes) and a merge fan-in of at least {MinMergeFanIn}, at parallelism " +
            $"{parallelism}. {minimum}");
    }

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

        int floorReadAheadBufferSize = MinimumWindow(maxLineLength);
        int floorDescriptorCapacity = floorReadAheadBufferSize / assumedMeanLineLength;

        long floorBytesPerRunCursor =
            2L * floorReadAheadBufferSize + (long)floorDescriptorCapacity * descriptorSize;

        long floorBytesPerFanInSlot = floorBytesPerRunCursor + MemoryPlan.LoserTreeBytesPerFanInSlot;

        long fanInRoom = budgetBytes - OutputBufferSize;

        // Judge viability at one worker and the floor window so worker and window choices cannot move the minimum budget.
        long rawFanIn = fanInRoom < 0 ? 0 : fanInRoom / floorBytesPerFanInSlot;
        bool phaseTwoViable = rawFanIn >= MinMergeFanIn;

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

            long adjustedRoom = room - (long)n * MaxMergeFanIn * MemoryPlan.LoserTreeBytesPerFanInSlot;
            if (adjustedRoom <= 0)
            {
                return 0;
            }

            long denominator = (long)n * MaxMergeFanIn * (2L * assumedMeanLineLength + descriptorSize);
            return adjustedRoom * assumedMeanLineLength / denominator;
        }

        int mergeParallelism = 1;
        for (int n = Math.Min(parallelism, MaxMergeParallelism); n > 1; n--)
        {
            if (WindowFor(n) >= floorReadAheadBufferSize)
            {
                mergeParallelism = n;
                break;
            }
        }

        long mergeRoom = RoomFor(mergeParallelism);

        long rawWindow = mergeRoom <= 0 ? floorReadAheadBufferSize : WindowFor(mergeParallelism);
        long window = Math.Max(floorReadAheadBufferSize, Math.Min(rawWindow, MaxReadAheadBufferSize));

        long BytesPerRunCursor(long readAhead) =>
            2L * readAhead + (readAhead / assumedMeanLineLength) * descriptorSize;

        int readAheadBufferSize = (int)window;
        int readAheadDescriptorCapacity = (int)(window / assumedMeanLineLength);

        long bytesPerRunCursor = BytesPerRunCursor(window);
        long cursorAndMetadataCostAcrossWorkers = (long)mergeParallelism * (bytesPerRunCursor + MemoryPlan.LoserTreeBytesPerFanInSlot);

        int mergeFanIn = mergeRoom < cursorAndMetadataCostAcrossWorkers
            ? 0
            : (int)Math.Min(MaxMergeFanIn, mergeRoom / cursorAndMetadataCostAcrossWorkers);

        // Must stay in step with MemoryPlan.WorstCasePhaseOneBytes: (p + 2) * C * (1 + D/m) + C + p * SpillBufferSize.
        long spillRoom = budgetBytes - (long)parallelism * SpillBufferSize;
        long denominator = (long)(parallelism + 2) * (assumedMeanLineLength + descriptorSize) + assumedMeanLineLength;
        long rawChunkSize = spillRoom <= 0 ? 0 : spillRoom * assumedMeanLineLength / denominator;

        int chosenChunkSize = rawChunkSize > Array.MaxLength ? Array.MaxLength : (int)rawChunkSize;

        // ChunkReader reserves the floor window before prefetching, so the chunk must exceed it.
        if (chosenChunkSize <= floorReadAheadBufferSize || !phaseTwoViable)
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

    // Bisection is valid only because viability is monotone in the budget.
    internal static bool TryFindMinimumViableBudget(
        int parallelism, int maxLineLength, int assumedMeanLineLength, out long minimumBudget)
    {
        minimumBudget = 0;
        long unviable = OutputBufferSize;
        long viable = MaxPlannedBudgetBytes;
        if (!TryCalculate(viable, parallelism, maxLineLength, assumedMeanLineLength, out _))
        {
            return false;
        }

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

        minimumBudget = viable;
        return true;
    }

    internal static long MinimumViableBudget(int parallelism, int maxLineLength, int assumedMeanLineLength) =>
        TryFindMinimumViableBudget(parallelism, maxLineLength, assumedMeanLineLength, out long minimumBudget)
            ? minimumBudget
            : throw new ArgumentOutOfRangeException(nameof(maxLineLength), maxLineLength,
                $"No memory budget can support a maximum line length of {maxLineLength} bytes at parallelism {parallelism}.");

    internal static int AssumedMeanFor(int maxLineLength) => Math.Min(AssumedMeanLineLength, maxLineLength);

    private static int MinimumWindow(int maxLineLength)
    {
        long result = (long)maxLineLength + LineCursor.WindowSlack;
        if (result > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLineLength), maxLineLength,
                $"The maximum line length is too large: adding the read window's slack ({LineCursor.WindowSlack}) would not fit in a 32-bit size.");
        }

        return (int)result;
    }
}
