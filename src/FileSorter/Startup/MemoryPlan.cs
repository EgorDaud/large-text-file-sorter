using System.Runtime.CompilerServices;
using FileSorter.LineFormat;
using FileSorter.Merging;

namespace FileSorter.Startup;

internal readonly record struct MemoryPlan(
    int ChunkSize,
    int DescriptorCapacity,      // descriptors allocated per pool slot
    int Parallelism,
    int MergeFanIn,
    int ReadAheadBufferSize,
    int ReadAheadDescriptorCapacity,   // descriptors allocated per run cursor
    int OutputBufferSize,
    int SpillBufferSize,
    int MergeParallelism)        // merge workers, each with its own cursors and output buffer
{
    // Derive this from the struct so the budget tracks layout changes.
    public static int DescriptorSize => Unsafe.SizeOf<LineDescriptor>();

    // One parsing slot, one prefetched slot, and one per active worker.
    public int PoolCapacity   => Parallelism + 2;

    // Oversized carry can displace one fill of already-read bytes. This side buffer holds
    // that data until the following fill consumes it.
    public long PendingBytesSize => ChunkSize;

    public long BytesPerSlot =>
        (long)ChunkSize + (long)DescriptorCapacity * DescriptorSize;

    // Each concurrent spill owns its staging buffer; FileStream adds no second buffer.
    public long WorstCasePhaseOneBytes =>
        (long)PoolCapacity * BytesPerSlot + PendingBytesSize + (long)Parallelism * SpillBufferSize;

    // A cursor owns two read-ahead windows so it can fill one while scanning the other.
    // Only the delivered window needs descriptors.
    public long BytesPerRunCursor =>
        2L * ReadAheadBufferSize + (long)ReadAheadDescriptorCapacity * DescriptorSize;

    // Each worker opens up to MergeFanIn cursors and owns an output buffer. MemoryBudget
    // sizes each read-ahead window per worker. Derive loser-tree storage from RunHead.
    public static int LoserTreeBytesPerFanInSlot => Unsafe.SizeOf<KWayMerge.RunHead>() + sizeof(int);

    // One RangePartition.RunOffsets entry is a long.
    public const int PartitionOffsetEntrySize = sizeof(long);

    // Persistent phase-two metadata: one loser tree per worker and, for partitioned
    // merges, one shared run-offset table. Sequential merges need no offset table.
    public long MergeMetadataBytes =>
        (long)MergeParallelism * MergeFanIn * LoserTreeBytesPerFanInSlot
        + (MergeParallelism > 1 ? (long)MergeFanIn * (MergeParallelism + 1) * PartitionOffsetEntrySize : 0);

    // Upper bound for concurrent cursor, output, and metadata storage. Sampling buffers
    // are released before workers start; array headers and stack are outside this model.
    public long WorstCasePhaseTwoBytes =>
        (long)MergeParallelism * MergeFanIn * BytesPerRunCursor
        + (long)MergeParallelism * OutputBufferSize
        + MergeMetadataBytes;
}
