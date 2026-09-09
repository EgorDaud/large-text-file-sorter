using System.Globalization;
using CsCheck;
using FileSorter.Merging;
using FileSorter.Startup;
using Xunit;

namespace FileSorter.Tests.Properties;

/// <summary>
/// The range-partitioned merge produces exactly the bytes the sequential merge produces,
/// over random run sets and random worker counts.
///
/// Every part of the partition is a place a line can be lost or emitted twice -- a
/// splitter located one line late in one run and on time in another, a slice boundary
/// landing exactly on a line start, an empty slice, a worker whose predicted output
/// length disagrees with what it wrote -- and all of those still produce an output that
/// is sorted and of plausible length. Only byte-identity against a merge that does none
/// of it rejects them.
///
/// Each scenario is merged twice over two identical copies of the same run files (the
/// executor deletes its inputs, so the second copy is what makes the second merge
/// possible), once at one worker and once at the drawn worker count, and both are also
/// checked against <see cref="NaiveReferenceSort"/>: two merges sharing a defect would
/// agree with each other and prove nothing.
///
/// The generator is shaped for the hard cases: a small vocabulary of numbers and string
/// parts, so byte-identical lines land in different runs and a splitter is routinely a
/// line that also appears many times over; run and entry counts low enough that a worker
/// count of 8 regularly exceeds the line count outright; and empty runs, whenever the
/// bucket draw gives a run nothing.
/// </summary>
public sealed class PartitionedMergePropertyTests : IDisposable
{
    private const int MaxLineLength = 32;
    private const int WindowSize = MaxLineLength + 2;
    private const int DescriptorCapacity = 3;
    private const int OutputBufferSize = 64;

    // Above every run count the scenario can draw, so MergePlanner always plans a single
    // pass over one group, which is the only shape the partition applies to.
    private const int MergeFanIn = 128;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "FileSorterTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // A deliberately tiny vocabulary: drawing numbers and string parts freely essentially
    // never produces the case this property is about, a splitter key that many lines are
    // exactly equal to in several runs at once. Eight numbers and six string parts give
    // 48 distinct lines across up to 80 entries, so duplicates are the rule.
    private static readonly Gen<long> Number = Gen.Long[0, 7];

    // "a\r" is in the vocabulary on purpose: a run file's '\r' before '\n' is content,
    // never a terminator, and this fixed vocabulary is the only way that shape reaches
    // the partitioned merge. LineEntryGen.BuildInput writes the second '\r' that keeps
    // it content whenever a string part ends in one.
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
        await Scenario.SampleAsync(async scenario =>
        {
            (List<(long Number, string StringPart)> entries, int runCount, int workerCount, int[] bucket) = scenario;
            CancellationToken ct = TestContext.Current.CancellationToken;
            string caseDirectory = Path.Combine(
                _directory, Interlocked.Increment(ref iteration).ToString(CultureInfo.InvariantCulture));

            // Splitting the multiset by bucket index, rather than slicing one sorted
            // sequence, is what puts byte-identical lines in different runs; a slice of
            // a sorted sequence essentially never does.
            List<(long, string)>[] perRun = [.. Enumerable.Range(0, runCount).Select(_ => new List<(long, string)>())];
            for (int i = 0; i < entries.Count; i++)
            {
                perRun[bucket[i]].Add(entries[i]);
            }

            byte[][] rawRunBytes = [.. perRun.Select(entries => LineEntryGen.BuildInput(entries))];
            byte[][] sortedRunBytes = [.. rawRunBytes.Select(NaiveReferenceSort.Sort)];
            byte[] expected = NaiveReferenceSort.Sort([.. rawRunBytes.SelectMany(b => b)]);

            byte[] sequential = await MergeAsync(
                Path.Combine(caseDirectory, "sequential"), sortedRunBytes, mergeParallelism: 1, ct);
            byte[] partitioned = await MergeAsync(
                Path.Combine(caseDirectory, "partitioned"), sortedRunBytes, workerCount, ct);

            Assert.Equal(expected, sequential);
            Assert.Equal(expected, partitioned);
            Assert.Equal(sequential, partitioned);
        }, iter: 300);
    }

    private static async Task<byte[]> MergeAsync(
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

        MemoryPlan plan = new(
            ChunkSize: 1024,
            DescriptorCapacity: 16,
            Parallelism: 1,
            MergeFanIn: MergeFanIn,
            ReadAheadBufferSize: WindowSize,
            ReadAheadDescriptorCapacity: DescriptorCapacity,
            OutputBufferSize: OutputBufferSize,
            SpillBufferSize: 64,
            MergeParallelism: mergeParallelism);

        MergeExecutor executor = new(runs, plan, MaxLineLength);
        string outputPath = Path.Combine(directory, "output.tmp");
        await executor.ExecuteAsync(runPaths, outputPath, ct);

        Assert.Equal(1, executor.PassesExecuted);

        // The partitioned path is only claimed when it was actually taken: a run set
        // holding no bytes at all has no partition to locate, and the executor falls back
        // to the sequential merge, which is correct and merely slower.
        long totalBytes = runBytes.Sum(b => (long)b.Length);
        int expectedWorkers = mergeParallelism > 1 && totalBytes > 0 ? mergeParallelism : 1;
        Assert.Equal(expectedWorkers, executor.MergeParallelismUsed);

        return await File.ReadAllBytesAsync(outputPath, ct);
    }
}
