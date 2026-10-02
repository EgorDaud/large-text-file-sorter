using System.Runtime.CompilerServices;
using FileSorter.LineFormat;
using FileSorter.Planning;
using Xunit;

namespace FileSorter.Tests.Planning;

public sealed class MemoryPlanTests
{
    [Fact]
    public void Derived_members_compute_from_the_primary_constructor_alone()
    {
        MemoryPlan plan = new(
            ChunkSize: 1000,
            DescriptorCapacity: 10,
            Parallelism: 3,
            MergeFanIn: 8,
            ReadAheadBufferSize: 64,
            ReadAheadDescriptorCapacity: 4,
            OutputBufferSize: 128,
            SpillBufferSize: 200,
            MergeParallelism: 3);

        Assert.Equal(Unsafe.SizeOf<LineDescriptor>(), MemoryPlan.DescriptorSize);
        Assert.Equal(5, plan.PoolCapacity);
        Assert.Equal(1000, plan.PendingBytesSize);

        long expectedBytesPerSlot = 1000L + 10L * MemoryPlan.DescriptorSize;
        Assert.Equal(expectedBytesPerSlot, plan.BytesPerSlot);

        long expectedPhaseOne = 5L * expectedBytesPerSlot + 1000L + 3L * 200L;
        Assert.Equal(expectedPhaseOne, plan.WorstCasePhaseOneBytes);

        long expectedBytesPerRunCursor = 2L * 64L + 4L * MemoryPlan.DescriptorSize;
        Assert.Equal(expectedBytesPerRunCursor, plan.BytesPerRunCursor);

        long expectedMetadata = 3L * 8L * MemoryPlan.LoserTreeBytesPerFanInSlot
            + 8L * (3L + 1L) * MemoryPlan.PartitionOffsetEntrySize;
        Assert.Equal(expectedMetadata, plan.MergeMetadataBytes);

        long expectedPhaseTwo = 3L * 8L * expectedBytesPerRunCursor + 3L * 128L + expectedMetadata;
        Assert.Equal(expectedPhaseTwo, plan.WorstCasePhaseTwoBytes);

        MemoryPlan sequential = plan with { MergeParallelism = 1 };
        long expectedSequentialMetadata = 1L * 8L * MemoryPlan.LoserTreeBytesPerFanInSlot;
        Assert.Equal(expectedSequentialMetadata, sequential.MergeMetadataBytes);
        Assert.Equal(8L * expectedBytesPerRunCursor + 128L + expectedSequentialMetadata, sequential.WorstCasePhaseTwoBytes);
    }

    [Fact]
    public void LoserTreeBytesPerFanInSlot_matches_RunHeads_own_measured_size_plus_one_int()
    {
        Assert.Equal(40, Unsafe.SizeOf<RunHead>()); // byte[]? reference (8) + LineDescriptor (32)
        Assert.Equal(Unsafe.SizeOf<RunHead>() + sizeof(int), MemoryPlan.LoserTreeBytesPerFanInSlot);
    }
}
