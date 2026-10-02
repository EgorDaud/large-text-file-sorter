using System.Text;
using CsCheck;
using FileSorter.LineFormat;
using FileSorter.Merging;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.Merging;

public sealed class RangePartitionerTests : IDisposable
{
    private const int MaxLineLength = 64;
    private const int Parallelism = 4;

    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    [Trait("Case", "RP-01")]
    public void Every_located_offset_is_a_line_start_and_the_slices_cover_each_run_exactly()
    {
        List<byte[]> runs = SortedRuns(runCount: 5, linesPerRun: 200, distinctKeys: 400);
        IReadOnlyList<string> paths = Write(runs);

        RangePartition partition = Locate(paths, workerCount: 4);

        AssertWellFormed(partition, runs);
    }

    [Fact]
    [Trait("Case", "RP-02")]
    public void Every_line_of_an_earlier_slice_is_strictly_below_every_line_of_a_later_one()
    {
        List<byte[]> runs = SortedRuns(runCount: 6, linesPerRun: 300, distinctKeys: 90);
        IReadOnlyList<string> paths = Write(runs);

        RangePartition partition = Locate(paths, workerCount: 5);

        AssertKeyOrdered(partition, runs);
    }

    [Fact]
    [Trait("Case", "RP-03")]
    public void A_run_of_byte_identical_lines_lands_wholly_in_one_worker()
    {
        byte[] identical = Lines(Enumerable.Repeat("7. same", 400));
        IReadOnlyList<string> paths = Write([identical, identical, identical]);

        RangePartition partition = Locate(paths, workerCount: 4);

        AssertWellFormed(partition, [identical, identical, identical]);
        Assert.Equal(0, partition.SliceBytes[0]);
        Assert.Equal(0, partition.SliceBytes[1]);
        Assert.Equal(0, partition.SliceBytes[2]);
        Assert.Equal(partition.TotalBytes, partition.SliceBytes[3]);
        Assert.Equal(double.PositiveInfinity, partition.Imbalance);
    }

    [Fact]
    [Trait("Case", "RP-04")]
    public void More_workers_than_lines_still_yields_a_partition_that_covers_every_byte()
    {
        List<byte[]> runs = [Lines(["1. a"]), Lines(["2. b"]), Lines(["3. c"])];
        IReadOnlyList<string> paths = Write(runs);

        RangePartition partition = Locate(paths, workerCount: 8);

        AssertWellFormed(partition, runs);
        AssertKeyOrdered(partition, runs);

        Assert.True(
            partition.SliceBytes.Count(bytes => bytes == 0) >= 5,
            "Expected at least five of the eight workers to receive an empty slice.");
    }

    [Fact]
    [Trait("Case", "RP-05")]
    public void Duplicate_splitters_produce_empty_slices_rather_than_overlapping_ones()
    {
        List<byte[]> runs =
        [
            Lines(Enumerable.Repeat("1. aaa", 50).Concat(Enumerable.Repeat("2. bbb", 50))),
            Lines(Enumerable.Repeat("1. aaa", 60).Concat(Enumerable.Repeat("2. bbb", 40))),
        ];
        IReadOnlyList<string> paths = Write(runs);

        RangePartition partition = Locate(paths, workerCount: 5);

        AssertWellFormed(partition, runs);
        AssertKeyOrdered(partition, runs);
        Assert.Contains(partition.SliceBytes, bytes => bytes == 0);
    }

    [Fact]
    [Trait("Case", "RP-06")]
    public void A_malformed_run_line_surfaces_as_a_malformed_line_exception_not_an_aggregate()
    {
        List<byte[]> runs = [Lines(["1. a", "not a line at all", "3. c"]), Lines(["2. b"])];
        IReadOnlyList<string> paths = Write(runs);

        MalformedLineException ex = Assert.Throws<MalformedLineException>(
            () => RangePartitioner.Locate(paths, workerCount: 3, MaxLineLength, Parallelism, TestContext.Current.CancellationToken));

        // "1. a\n" is 5 bytes.
        Assert.Contains(paths[0], ex.Message, StringComparison.Ordinal);
        Assert.Equal(5, ex.ByteOffset);
        Assert.Equal(MalformedLineException.LineNumberUnavailable, ex.LineNumber);
    }

    [Fact]
    [Trait("Case", "RP-07")]
    public void Empty_runs_contribute_nothing_and_runs_holding_no_bytes_at_all_yield_no_partition()
    {
        List<byte[]> withEmpties = [[], Lines(["1. a", "3. c"]), [], Lines(["2. b"]), []];
        IReadOnlyList<string> paths = Write(withEmpties);

        RangePartition partition = Locate(paths, workerCount: 3);
        AssertWellFormed(partition, withEmpties);
        AssertKeyOrdered(partition, withEmpties);

        IReadOnlyList<string> allEmpty = Write([[], []]);
        Assert.Null(RangePartitioner.Locate(
            allEmpty, workerCount: 4, MaxLineLength, Parallelism, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Case", "RP-08")]
    public void Random_run_sets_at_random_worker_counts_are_always_well_formed_and_key_ordered()
    {
        Gen<(int RunCount, int LinesPerRun, int DistinctKeys, int WorkerCount, int Seed)> scenario =
            Gen.Select(Gen.Int[1, 6], Gen.Int[0, 120], Gen.Int[1, 200], Gen.Int[2, 8], Gen.Int);

        scenario.Sample(draw =>
        {
            List<byte[]> runs = SortedRuns(draw.RunCount, draw.LinesPerRun, draw.DistinctKeys, draw.Seed);
            IReadOnlyList<string> paths = Write(runs);

            RangePartition? located = RangePartitioner.Locate(
                paths, draw.WorkerCount, MaxLineLength, Parallelism, TestContext.Current.CancellationToken);
            if (located is not { } partition)
            {
                Assert.All(runs, run => Assert.Empty(run));
                return;
            }

            AssertWellFormed(partition, runs);
            AssertKeyOrdered(partition, runs);
        }, iter: 200);
    }

    [Fact]
    [Trait("Case", "RP-09")]
    public void A_sample_cut_short_by_its_byte_budget_still_yields_the_same_well_formed_partition_every_time()
    {
        const int lineLength = 8 * 1024;
        const int linesPerRun = 1100;
        Random random = new(2024);
        List<byte[]> runs = [];
        for (int r = 0; r < 4; r++)
        {
            List<string> lines = [];
            for (int i = 0; i < linesPerRun; i++)
            {
                string head = $"{random.Next(97)}. k{random.Next(1_000_000):D6}";
                lines.Add(head.PadRight(lineLength - 1, 'x'));
            }

            lines.Sort(NaiveReferenceSort.LineComparer);
            runs.Add(Lines(lines));
        }

        long totalBytes = runs.Sum(run => (long)run.Length);
        Assert.True(totalBytes / RangePartitioner.TargetSampleLines >= lineLength, "draws must land on distinct lines");
        Assert.True((long)RangePartitioner.TargetSampleLines * lineLength >= 2L * RangePartitioner.SampleLineByteBudget);
        IReadOnlyList<string> paths = Write(runs);

        RangePartition[] partitions = [.. Enumerable.Range(0, 3).Select(_ => RangePartitioner.Locate(
            paths, workerCount: 4, maxLineLength: 2 * lineLength, parallelism: 8, TestContext.Current.CancellationToken)!)];

        AssertWellFormed(partitions[0], runs);
        AssertKeyOrdered(partitions[0], runs);
        Assert.Equal(4, partitions[0].NonEmptySlices);
        foreach (RangePartition other in partitions[1..])
        {
            Assert.Equal(partitions[0].SliceBytes, other.SliceBytes);
            for (int r = 0; r < runs.Count; r++)
            {
                Assert.Equal(partitions[0].RunOffsets[r], other.RunOffsets[r]);
            }
        }
    }

    private static RangePartition Locate(IReadOnlyList<string> paths, int workerCount)
    {
        RangePartition? partition = RangePartitioner.Locate(
            paths, workerCount, MaxLineLength, Parallelism, TestContext.Current.CancellationToken);
        Assert.NotNull(partition);
        return partition;
    }

    private static void AssertWellFormed(RangePartition partition, IReadOnlyList<byte[]> runs)
    {
        long total = 0;
        for (int r = 0; r < runs.Count; r++)
        {
            HashSet<long> lineStarts = LineStartsOf(runs[r]);
            long[] offsets = partition.RunOffsets[r];

            Assert.Equal(0, offsets[0]);
            Assert.Equal(runs[r].Length, offsets[partition.WorkerCount]);

            long previous = 0;
            for (int w = 0; w <= partition.WorkerCount; w++)
            {
                Assert.True(offsets[w] >= previous, $"Run {r}'s offsets are not monotone at worker {w}.");
                Assert.True(
                    lineStarts.Contains(offsets[w]),
                    $"Run {r}'s offset {offsets[w]} for worker {w} is not a line start.");
                previous = offsets[w];
            }

            total += runs[r].Length;
        }

        Assert.Equal(total, partition.TotalBytes);
        Assert.Equal(total, partition.SliceBytes.Sum());

        long running = 0;
        for (int w = 0; w < partition.WorkerCount; w++)
        {
            Assert.Equal(running, partition.OutputOffsets[w]);
            running += partition.SliceBytes[w];
        }
    }

    private static void AssertKeyOrdered(RangePartition partition, IReadOnlyList<byte[]> runs)
    {
        List<string>[] perWorker = [.. Enumerable.Range(0, partition.WorkerCount).Select(_ => new List<string>())];
        for (int r = 0; r < runs.Count; r++)
        {
            for (int w = 0; w < partition.WorkerCount; w++)
            {
                long start = partition.RunOffsets[r][w];
                long end = partition.RunOffsets[r][w + 1];
                perWorker[w].AddRange(LinesIn(runs[r], start, end));
            }
        }

        Assert.Equal(
            runs.Sum(run => LinesIn(run, 0, run.Length).Count),
            perWorker.Sum(lines => lines.Count));

        for (int w = 1; w < partition.WorkerCount; w++)
        {
            if (perWorker[w].Count == 0)
            {
                continue;
            }

            string smallestHere = perWorker[w].Min(NaiveReferenceSort.LineComparer)!;
            for (int earlier = 0; earlier < w; earlier++)
            {
                foreach (string line in perWorker[earlier])
                {
                    Assert.True(
                        NaiveReferenceSort.LineComparer.Compare(line, smallestHere) < 0,
                        $"'{line}' in worker {earlier} is not strictly below '{smallestHere}' in worker {w}.");
                }
            }
        }
    }

    private static HashSet<long> LineStartsOf(byte[] run)
    {
        HashSet<long> starts = [0, run.Length];
        for (int i = 0; i < run.Length; i++)
        {
            if (run[i] == (byte)'\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static List<string> LinesIn(byte[] run, long start, long end)
    {
        string slice = Encoding.ASCII.GetString(run, (int)start, (int)(end - start));
        return [.. slice.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
    }

    private static byte[] Lines(IEnumerable<string> lines)
    {
        StringBuilder content = new();
        foreach (string line in lines)
        {
            content.Append(line).Append('\n');
        }

        return Encoding.ASCII.GetBytes(content.ToString());
    }

    private static List<byte[]> SortedRuns(int runCount, int linesPerRun, int distinctKeys, int seed = 12345)
    {
        Random random = new(seed);
        List<byte[]> runs = [];
        for (int r = 0; r < runCount; r++)
        {
            List<string> lines = [];
            for (int i = 0; i < linesPerRun; i++)
            {
                int key = random.Next(distinctKeys);
                lines.Add($"{key % 97}. k{key:D4}");
            }

            lines.Sort(NaiveReferenceSort.LineComparer);
            runs.Add(Lines(lines));
        }

        return runs;
    }

    private IReadOnlyList<string> Write(IReadOnlyList<byte[]> runs)
    {
        List<string> paths = [];
        string caseDirectory = Path.Combine(_directory.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(caseDirectory);
        for (int r = 0; r < runs.Count; r++)
        {
            string path = Path.Combine(caseDirectory, $"run-{r:D4}.tmp");
            File.WriteAllBytes(path, runs[r]);
            paths.Add(path);
        }

        return paths;
    }
}
