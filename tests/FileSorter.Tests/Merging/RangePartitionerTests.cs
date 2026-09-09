using System.Text;
using CsCheck;
using FileSorter.LineFormat;
using FileSorter.Merging;
using FileSorter.Tests.Properties;
using Xunit;

namespace FileSorter.Tests.Merging;

/// <summary>
/// The range partition itself, checked as a value rather than through the merge that
/// consumes it. The properties are: every located offset is a line start, the offsets are
/// monotone, the slice lengths sum to the run length, and the keys are ordered across
/// workers.
///
/// That last one is checked in the form that does not need the splitter keys themselves --
/// every line in an earlier slice is strictly less than every line in a later one, across
/// all runs at once -- which is equivalent to comparing each run against the splitter and
/// strictly stronger. Equivalent, because one splitter is used for every run: a line
/// before a run's boundary is below the splitter and a line at or after any run's boundary
/// is at or above it, so the two sets are ordered by the splitter that separates them.
/// Stronger, because it also pins the tie rule -- a run of byte-identical lines lands
/// wholly in the later worker -- since a copy of a tied line left on the earlier side
/// would make the earlier side's maximum equal the later side's minimum, and this
/// comparison would then not be strict.
/// </summary>
public sealed class RangePartitionerTests : IDisposable
{
    private const int MaxLineLength = 64;

    // An arbitrary, small value: these fixtures are tiny, so the searches' degree of
    // parallelism makes no observable difference here beyond exercising a value other
    // than the default.
    private const int Parallelism = 4;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "FileSorterTests", Guid.NewGuid().ToString("N"));

    public RangePartitionerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

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
        // The adversarial shape: one key, so no splitter can separate anything. Every
        // splitter is that same line, every "first line at or above the splitter" is
        // offset zero, and the last worker takes the lot. The partition is still a
        // partition and the merge over it is still correct, but the imbalance is
        // unbounded, and the executor reports it rather than hiding it.
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
        // Eight workers over three lines: at most three slices can hold anything, and the
        // rest are empty. Nothing downstream treats an empty slice specially -- it simply
        // opens no handle for it (MergeExecutor.MergeSliceAsync) -- so the only thing to
        // pin here is that the partition is still complete.
        List<byte[]> runs = [Lines(["1. a"]), Lines(["2. b"]), Lines(["3. c"])];
        IReadOnlyList<string> paths = Write(runs);

        RangePartition partition = Locate(paths, workerCount: 8);

        AssertWellFormed(partition, runs);
        AssertKeyOrdered(partition, runs);

        // Eight workers with at most three lines between them leave at least five empty
        // slices, asserted rather than left as a property of the fixture.
        Assert.True(
            partition.SliceBytes.Count(bytes => bytes == 0) >= 5,
            "Expected at least five of the eight workers to receive an empty slice.");
    }

    [Fact]
    [Trait("Case", "RP-05")]
    public void Duplicate_splitters_produce_empty_slices_rather_than_overlapping_ones()
    {
        // Two distinct keys and five workers, so at least three of the four splitters are
        // byte-identical to another. A duplicate splitter has to give its worker an empty
        // slice; the failure it guards against is two workers being handed the SAME range,
        // which would duplicate every line in it and still leave a sorted output.
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
        // The searches run on several threads at once, so without unwrapping,
        // Parallel.For would hand Program an AggregateException where its own exit-code
        // mapping expects the malformed-line exception itself.
        List<byte[]> runs = [Lines(["1. a", "not a line at all", "3. c"]), Lines(["2. b"])];
        IReadOnlyList<string> paths = Write(runs);

        MalformedLineException ex = Assert.Throws<MalformedLineException>(
            () => RangePartitioner.Locate(paths, workerCount: 3, MaxLineLength, Parallelism, TestContext.Current.CancellationToken));

        // The exception has to carry the real run path and the real byte offset
        // ("1. a\n" is 5 bytes), or it cannot tell an operator which run, or where in
        // it, the malformed line was. The line number stays unavailable rather than a
        // misleading 0: this class never counts lines, only byte offsets.
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

        // Nothing at all to partition: the caller merges sequentially instead, which is
        // correct for any input.
        IReadOnlyList<string> allEmpty = Write([[], []]);
        Assert.Null(RangePartitioner.Locate(
            allEmpty, workerCount: 4, MaxLineLength, Parallelism, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Case", "RP-08")]
    public void Random_run_sets_at_random_worker_counts_are_always_well_formed_and_key_ordered()
    {
        // The curated cases above pin shapes someone thought of. This sweeps the run
        // count, the lines per run, how many distinct keys there are (which is what
        // decides whether splitters collide) and the worker count together, since the
        // interesting failures live in their combinations rather than in any one.
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
                // Only reachable when every run is empty, in which case there is nothing
                // for the claims below to be about.
                Assert.All(runs, run => Assert.Empty(run));
                return;
            }

            AssertWellFormed(partition, runs);
            AssertKeyOrdered(partition, runs);
        }, iter: 200);
    }

    private static RangePartition Locate(IReadOnlyList<string> paths, int workerCount)
    {
        RangePartition? partition = RangePartitioner.Locate(
            paths, workerCount, MaxLineLength, Parallelism, TestContext.Current.CancellationToken);
        Assert.NotNull(partition);
        return partition;
    }

    // Line starts, monotone offsets, complete cover, and the slice arithmetic.
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

        // Every line the partition placed is still there, exactly once.
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

    // `runCount` runs, each independently sorted, drawing from `distinctKeys` distinct
    // lines -- a small key space is what makes byte-identical lines land in several runs
    // and makes two splitters collide.
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
        string caseDirectory = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
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
