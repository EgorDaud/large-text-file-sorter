using FileSorter.Planning;

namespace FileSorter.Tests.Support;

// Far below what MemoryBudget would pick, so tiny fixtures still cross window and descriptor limits.
internal static class TestPlans
{
    // 65-byte windows suit a maxLineLength of 63: the line, its CR and one fresh byte.
    internal static MemoryPlan Merge(int fanIn, int mergeParallelism = 1) => new(
        ChunkSize: 1024,
        DescriptorCapacity: 16,
        Parallelism: 1,
        MergeFanIn: fanIn,
        ReadAheadBufferSize: 65,
        ReadAheadDescriptorCapacity: 8,
        OutputBufferSize: 64,
        SpillBufferSize: 64,
        MergeParallelism: mergeParallelism);
}
