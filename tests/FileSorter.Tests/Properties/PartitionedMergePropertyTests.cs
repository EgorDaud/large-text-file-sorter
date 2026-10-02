using System.Globalization;
using CsCheck;
using FileSorter.Infrastructure;
using FileSorter.Merging;
using FileSorter.Planning;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.Properties;

public sealed class PartitionedMergePropertyTests : IDisposable
{
    private const int MaxLineLength = 32;
    private const int WindowSize = MaxLineLength + 2;
    private const int DescriptorCapacity = 3;
    private const int OutputBufferSize = 64;

    // Above any drawn run count, so the plan is one single-pass group, the only shape that partitions.
    private const int MergeFanIn = 128;

    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    // Deliberately tiny vocabulary, so splitter keys repeat across several runs.
    private static readonly Gen<long> Number = Gen.Long[0, 7];

    // "a\r" puts a content '\r' before '\n' in a run file, the only way that shape reaches this merge.
    private static readonly Gen<string> StringPart = Gen.OneOfConst("a", "b", "cc", "dd", "eee", "fff", "a\r");
    private static readonly Gen<(long Number, string StringPart)> Entry = Gen.Select(Number, StringPart);

    private static readonly Gen<(List<(long Number, string StringPart)> Entries, int RunCount, int WorkerCount, int[] Bucket)> Scenario =
        Gen.Select(Entry.List[0, 80], Gen.Int[2, 10], Gen.Int[1, 8]).SelectMany(t =>
            Gen.Int[0, t.Item2 - 1].Array[t.Item1.Count].Select(bucket => (t.Item1, t.Item2, t.Item3, bucket)));

    [Fact]
    [Trait("Case", "PB-12")]
    public async Task The_partitioned_merge_produces_the_same_bytes_as_the_sequential_one()
    {
        int iteration = 0;
        int partitionedDraws = 0;
        await Scenario.SampleAsync(async scenario =>
        {
            (List<(long Number, string StringPart)> entries, int runCount, int workerCount, int[] bucket) = scenario;
            CancellationToken ct = TestContext.Current.CancellationToken;
            string caseDirectory = Path.Combine(
                _directory.Path, Interlocked.Increment(ref iteration).ToString(CultureInfo.InvariantCulture));

            // Bucketing, not slicing a sorted sequence, is what splits identical lines across runs.
            List<(long, string)>[] perRun = [.. Enumerable.Range(0, runCount).Select(_ => new List<(long, string)>())];
            for (int i = 0; i < entries.Count; i++)
            {
                perRun[bucket[i]].Add(entries[i]);
            }

            byte[][] rawRunBytes = [.. perRun.Select(entries => LineEntryGen.BuildInput(entries))];
            byte[][] sortedRunBytes = [.. rawRunBytes.Select(NaiveReferenceSort.Sort)];
            byte[] expected = NaiveReferenceSort.Sort([.. rawRunBytes.SelectMany(b => b)]);

            (byte[] sequential, _) = await MergeAsync(
                Path.Combine(caseDirectory, "sequential"), sortedRunBytes, mergeParallelism: 1, ct);
            (byte[] partitioned, int workersUsed) = await MergeAsync(
                Path.Combine(caseDirectory, "partitioned"), sortedRunBytes, workerCount, ct);
            if (workersUsed > 1)
            {
                Interlocked.Increment(ref partitionedDraws);
            }

            Assert.Equal(expected, sequential);
            Assert.Equal(expected, partitioned);
            Assert.Equal(sequential, partitioned);
        }, iter: 300);

        Assert.True(partitionedDraws > 0, "No draw reached the partitioned path.");
    }

    private static async Task<(byte[] Output, int Workers)> MergeAsync(
        string directory, byte[][] runBytes, int mergeParallelism, CancellationToken ct)
    {
        using TemporaryRunSet runs = new(directory);
        List<string> runPaths = [];
        foreach (byte[] bytes in runBytes)
        {
            string path = runs.CreateRunPath();
            await File.WriteAllBytesAsync(path, bytes, ct);
            runPaths.Add(path);
        }

        MemoryPlan plan = TestPlans.Merge(MergeFanIn, mergeParallelism) with
        {
            ReadAheadBufferSize = WindowSize,
            ReadAheadDescriptorCapacity = DescriptorCapacity,
            OutputBufferSize = OutputBufferSize,
        };

        MergeExecutor executor = new(runs, plan, MaxLineLength);
        string outputPath = Path.Combine(directory, "output.tmp");
        await executor.ExecuteAsync(runPaths, outputPath, ct);

        Assert.Equal(1, executor.PassesExecuted);

        int workers = executor.Partition?.Workers ?? 1;
        Assert.True(workers == 1 || workers == mergeParallelism, $"Unexpected worker count {workers}.");

        if (workers == 1 && mergeParallelism > 1 && runBytes.Sum(b => (long)b.Length) > 0)
        {
            Assert.NotNull(executor.Partition);
            Assert.Equal(PartitionOutcome.SingleSlice, executor.Partition.Value.Outcome);
        }

        return (await File.ReadAllBytesAsync(outputPath, ct), workers);
    }
}
