using System.Runtime.CompilerServices;
using FileSorter.LineFormat;

namespace FileSorter.Planning;

internal readonly record struct MemoryPlan(
    int ChunkSize,
    int DescriptorCapacity,
    int Parallelism,
    int MergeFanIn,
    int ReadAheadBufferSize,
    int ReadAheadDescriptorCapacity,
    int OutputBufferSize,
    int SpillBufferSize,
    int MergeParallelism)
{
    public static int DescriptorSize => Unsafe.SizeOf<LineDescriptor>();

    // One parsing slot, one prefetched slot, and one per active worker.
    public int PoolCapacity => Parallelism + 2;

    // Oversized carry can displace one fill of already-read bytes, held here until the next fill.
    public long PendingBytesSize => ChunkSize;

    public long BytesPerSlot =>
        (long)ChunkSize + (long)DescriptorCapacity * DescriptorSize;

    public long WorstCasePhaseOneBytes =>
        (long)PoolCapacity * BytesPerSlot + PendingBytesSize + (long)Parallelism * SpillBufferSize;

    public long BytesPerRunCursor =>
        2L * ReadAheadBufferSize + (long)ReadAheadDescriptorCapacity * DescriptorSize;

    public static int LoserTreeBytesPerFanInSlot => Unsafe.SizeOf<RunHead>() + sizeof(int);

    public const int PartitionOffsetEntrySize = sizeof(long);

    public long MergeMetadataBytes =>
        (long)MergeParallelism * MergeFanIn * LoserTreeBytesPerFanInSlot
        + (MergeParallelism > 1 ? (long)MergeFanIn * (MergeParallelism + 1) * PartitionOffsetEntrySize : 0);

    public long WorstCasePhaseTwoBytes =>
        (long)MergeParallelism * MergeFanIn * BytesPerRunCursor
        + (long)MergeParallelism * OutputBufferSize
        + MergeMetadataBytes;
}
