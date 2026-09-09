using System.Runtime.CompilerServices;
using FileSorter.LineFormat;
using FileSorter.Merging;
using FileSorter.Startup;
using Xunit;

namespace FileSorter.Tests.Startup;

// MemoryPlan's one rule: every derived quantity is a computed member of the primary
// constructor's parameters, never a parameter itself. These tests are that rule.
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
        Assert.Equal(5, plan.PoolCapacity);      // Parallelism + 2
        Assert.Equal(1000, plan.PendingBytesSize); // exactly ChunkSize

        long expectedBytesPerSlot = 1000L + 10L * MemoryPlan.DescriptorSize;
        Assert.Equal(expectedBytesPerSlot, plan.BytesPerSlot);

        long expectedPhaseOne = 5L * expectedBytesPerSlot + 1000L + 3L * 200L;
        Assert.Equal(expectedPhaseOne, plan.WorstCasePhaseOneBytes);

        // ReadAheadBufferSize counts twice: a cursor holds two equal-size windows so a
        // fill can be in flight while the other is scanned.
        long expectedBytesPerRunCursor = 2L * 64L + 4L * MemoryPlan.DescriptorSize;
        Assert.Equal(expectedBytesPerRunCursor, plan.BytesPerRunCursor);

        // MergeParallelism (3) multiplies both phase-two terms, not only the cursor one:
        // every worker opens every run of its group AND stages its own output through
        // its own OutputBufferSize array.
        //
        // MergeMetadataBytes is a third addend: three workers' own loser trees
        // (MergeParallelism * MergeFanIn * LoserTreeBytesPerFanInSlot) plus, since
        // MergeParallelism is above 1, the partition's offset table (MergeFanIn *
        // (MergeParallelism + 1) * PartitionOffsetEntrySize -- one shared array whose
        // WIDTH, not its whole size, scales with the worker count).
        long expectedMetadata = 3L * 8L * MemoryPlan.LoserTreeBytesPerFanInSlot
            + 8L * (3L + 1L) * MemoryPlan.PartitionOffsetEntrySize;
        Assert.Equal(expectedMetadata, plan.MergeMetadataBytes);

        long expectedPhaseTwo = 3L * 8L * expectedBytesPerRunCursor + 3L * 128L + expectedMetadata;
        Assert.Equal(expectedPhaseTwo, plan.WorstCasePhaseTwoBytes);

        // At one worker the loser-tree term is still live (KWayMerge.MergeAsync always
        // builds one) but the partition-offset term is exactly zero, because
        // MergeExecutor never partitions at a single worker.
        MemoryPlan sequential = plan with { MergeParallelism = 1 };
        long expectedSequentialMetadata = 1L * 8L * MemoryPlan.LoserTreeBytesPerFanInSlot;
        Assert.Equal(expectedSequentialMetadata, sequential.MergeMetadataBytes);
        Assert.Equal(8L * expectedBytesPerRunCursor + 128L + expectedSequentialMetadata, sequential.WorstCasePhaseTwoBytes);
    }

    [Fact]
    public void LoserTreeBytesPerFanInSlot_matches_RunHeads_own_measured_size_plus_one_int()
    {
        // MergeMetadataBytes prices KWayMerge.LoserTree's per-slot cost (one RunHead in
        // its _heads array, one int in its _tree array) into the phase-two budget.
        // KWayMerge.RunHead is `internal` rather than `private` so that this assertion
        // can measure the real struct directly: a field added to RunHead, or to the
        // LineDescriptor it embeds, fails here rather than silently under-pricing the
        // budget.
        Assert.Equal(40, Unsafe.SizeOf<KWayMerge.RunHead>()); // byte[]? reference (8) + LineDescriptor (32)
        Assert.Equal(Unsafe.SizeOf<KWayMerge.RunHead>() + sizeof(int), MemoryPlan.LoserTreeBytesPerFanInSlot);
    }
}
