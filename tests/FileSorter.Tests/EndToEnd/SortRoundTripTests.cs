using System.Globalization;
using System.Text;
using FileSorter.LineFormat;
using FileSorter.Merging;
using FileSorter.Startup;
using FileSorter.Verification;
using TestFileGenerator.Generation;
using Xunit;

namespace FileSorter.Tests.EndToEnd;

// Drives Program.RunAsync itself, the way the real binary's Main does, rather than any
// one phase in isolation: the exit codes, the absence of leftover run-*.tmp files, and
// each of the phase-two branches are checked here as a whole rather than one type at a
// time.
[Collection("Program")]
public sealed class SortRoundTripTests : IDisposable
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "FileSorterTests", Guid.NewGuid().ToString("N"));

    public SortRoundTripTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Sorting_an_empty_input_produces_an_empty_output_and_no_leftover_temp_files()
    {
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");
        File.WriteAllBytes(inputPath, []);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outputPath));
        Assert.Empty(File.ReadAllBytes(outputPath));
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    public async Task Sorting_with_a_max_line_below_the_internal_assumed_mean_does_not_crash()
    {
        // Program.AssumedMeanLineLength is a fixed constant (32) and
        // MemoryBudget.Calculate throws ArgumentOutOfRangeException unless
        // assumedMeanLineLength <= maxLineLength. --max-line has no floor, so a legal
        // value below that constant would reach Calculate unclamped and crash with an
        // unhandled exception instead of a documented exit code; RunAsync clamps the
        // assumption to maxLineLength to prevent it. MaxLineLength 20 stays below the
        // constant with room to spare.
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");
        File.WriteAllText(inputPath, "1. Apple\n2. Banana\n3. Cherry\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, MaxLineLength: 20, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        AssertIsSortedPermutationOfInput(inputPath, outputPath);
    }

    [Fact]
    public async Task Sorting_with_a_missing_output_directory_fails_fast_before_phase_one_starts()
    {
        // A missing output directory must be caught before phase one starts: left to
        // the eventual open, it surfaces as a DirectoryNotFoundException only after
        // every run file has been produced. Exit 3 is the "invalid arguments" row.
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "no-such-directory", "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");
        File.WriteAllText(inputPath, "1. Apple\n2. Banana\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(3, exitCode);
        Assert.False(Directory.Exists(tempDirectory)); // TemporaryRunSet is never even constructed
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task Sorting_with_an_unusable_temp_directory_fails_fast_before_phase_one_starts()
    {
        // --temp pointed at a path that already exists as an ordinary file:
        // Directory.CreateDirectory cannot create a directory there, and the resulting
        // IOException from inside TemporaryRunSet's constructor has to map to exit 3
        // rather than escape unhandled.
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp-is-actually-a-file");
        File.WriteAllText(inputPath, "1. Apple\n2. Banana\n");
        File.WriteAllText(tempDirectory, "not a directory");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(3, exitCode);
        Assert.False(File.Exists(outputPath)); // phase one never started
    }

    // Pipeline is internal, and a [Theory]'s parameters must be as accessible as
    // the public test method itself, so the pipeline choice travels as bool here
    // (true means Akka) and is mapped back just before use.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sorting_a_small_input_that_fits_in_one_run_leaves_no_temp_file_behind(bool useAkka)
    {
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");

        // A handful of lines is nowhere near ChunkSize at this budget, so ChunkReader
        // emits exactly one chunk, phase one emits exactly one run, and phase two takes
        // RunPlacement's single-run branch rather than MergeExecutor.
        File.WriteAllText(inputPath, "30. Cherry\n2. Apple\n100. Banana\n2. Aardvark\n");

        SorterOptions options = new(
            inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, useAkka ? Pipeline.Akka : Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        AssertIsSortedPermutationOfInput(inputPath, outputPath);
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sorting_forces_a_multi_pass_merge_when_the_memory_budget_is_small(bool useAkka)
    {
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");

        // The budget has to clear two fixed costs before it buys a single chunk byte:
        // phase two's 1 MiB output buffer, and one 64 KiB spill buffer per phase-one
        // worker. MinimumViableBudget for this (maxLine, assumedMean, parallelism)
        // triple is 1,050,120, almost all of it the output buffer, so 1,051,664 clears
        // it with a deliberately tight margin. Budget and parallelism are chosen
        // together, because MemoryBudget rejects a budget its parallelism cannot cover
        // rather than quietly dropping to one worker.
        // At 1,051,664: spillRoom = 1,051,664 - 2*65,536 = 920,592; chunkSize =
        // floor(920,592*32 / ((2+2)*(32+32)+32 = 288)) = floor(29,458,944/288) =
        // 102,288. Phase two: fanInRoom = 1,051,664 - 1,048,576 = 3,088;
        // floorBytesPerRunCursor = 2*258+8*32 = 772; fan-in = floor(3,088/772) = 4
        // exactly. A megabyte of short lines produces on the order of ten runs at this
        // chunk size -- the run count follows the real data's line-length distribution,
        // not the plan alone -- comfortably above a fan-in of 4, which is what forces
        // more than one merge pass.
        WriteGeneratedInput(inputPath, targetBytes: 1024 * 1024, seed: 7);

        SorterOptions options = new(
            inputPath, outputPath, tempDirectory, 1_051_664, 256, Parallelism: 2, useAkka ? Pipeline.Akka : Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        AssertIsSortedPermutationOfInput(inputPath, outputPath);
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    public async Task Both_pipelines_produce_byte_identical_output_for_a_multi_pass_run()
    {
        string inputPath = Path.Combine(_directory, "in.txt");
        WriteGeneratedInput(inputPath, targetBytes: 512 * 1024, seed: 13);

        string akkaOutput = Path.Combine(_directory, "akka-out.txt");
        string channelsOutput = Path.Combine(_directory, "channels-out.txt");

        // 1,051,664 is the same budget the multi-pass test above derives longhand: just
        // clear of this triple's MinimumViableBudget of 1,050,120. This test needs only
        // a small, multi-pass-forcing budget, not a specific fan-in.
        SorterOptions akkaOptions = new(
            inputPath, akkaOutput, Path.Combine(_directory, "akka-temp"), 1_051_664, 256, 2, Pipeline.Akka);
        SorterOptions channelsOptions = new(
            inputPath, channelsOutput, Path.Combine(_directory, "channels-temp"), 1_051_664, 256, 2, Pipeline.Channels);

        int akkaExit = await Program.RunAsync(akkaOptions, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        int channelsExit = await Program.RunAsync(channelsOptions, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, akkaExit);
        Assert.Equal(0, channelsExit);
        Assert.Equal(File.ReadAllBytes(akkaOutput), File.ReadAllBytes(channelsOutput));
    }

    [Fact]
    public async Task Cancellation_unwinds_cleanly_with_no_leftover_temp_files()
    {
        // This exercises the CancellationToken plumbing through RunAsync, not the
        // exit-130 mapping: that mapping lives in Main's own OperationCanceledException
        // catch, and no OS-level Ctrl+C can be delivered to a process from inside a
        // unit test.
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");
        WriteGeneratedInput(inputPath, targetBytes: 256 * 1024, seed: 3);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Program.RunAsync(options, cts.Token).WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    [Trait("Case", "ET-01")]
    public async Task Sorting_a_crlf_file_of_maximum_length_lines_gives_identical_output_at_two_memory_budgets()
    {
        // The trap: a fill boundary landing on the CR of a maximum-length \r\n line is
        // easily mistaken for a malformed, over-length line. Because the fill stride
        // walks that boundary through a different phase at every budget, such a defect
        // makes the same file sort correctly at one --memory and fail at another, which
        // is why this runs at two nearby budgets. --max-line 8 makes every line's
        // content exactly the limit, so the minimum viable budget's small chunk puts
        // the boundary on some line's CR across the twelve lines below either way.
        const int maxLineLength = 8;
        string inputPath = Path.Combine(_directory, "in.txt");

        // Descending, so the sort is not already a no-op; string part fixed ("abcd")
        // so LineOrder's tie-break falls to the number, giving one unambiguous
        // ascending order to check against. "21. abcd" etc. are each exactly 8 bytes.
        int[] numbers = [.. Enumerable.Range(10, 12).Reverse()];
        byte[] input = [.. numbers.SelectMany(n => Encoding.ASCII.GetBytes($"{n}. abcd\r\n"))];
        File.WriteAllBytes(inputPath, input);

        long minimumBudget = MemoryBudget.MinimumViableBudget(parallelism: 1, maxLineLength, assumedMeanLineLength: maxLineLength);
        byte[] expected = Encoding.ASCII.GetBytes(
            string.Concat(numbers.OrderBy(n => n).Select(n => $"{n}. abcd\n")));

        byte[]? previousOutput = null;
        foreach (long budget in new[] { minimumBudget, minimumBudget + 97 })
        {
            string outputPath = Path.Combine(_directory, $"out-{budget}.txt");
            string tempDirectory = Path.Combine(_directory, $"temp-{budget}");
            SorterOptions options = new(inputPath, outputPath, tempDirectory, budget, maxLineLength, Parallelism: 1, Pipeline.Channels);

            int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            byte[] output = File.ReadAllBytes(outputPath);
            Assert.DoesNotContain((byte)'\r', output); // run files, and this output, are '\n'-normalised
            Assert.Equal(expected, output);
            previousOutput ??= output;
            Assert.Equal(previousOutput, output);
        }
    }

    [Fact]
    [Trait("Case", "ET-02")]
    public async Task Sorting_an_input_whose_final_unterminated_line_ends_in_a_bare_carriage_return_strips_it()
    {
        // The '\r' at the end of the file is the first half of a "\r\n" terminator
        // whose '\n' never arrived, not content of "1. Apple". Keeping it as content
        // makes the output's own reader -- and --verify -- disagree with what the
        // sorter just wrote, so a correct sort reports a hash mismatch against its own
        // input.
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");
        File.WriteAllText(inputPath, "2. Banana\n1. Apple\r");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("1. Apple\n2. Banana\n", File.ReadAllText(outputPath));

        VerifyOptions verifyOptions = new(inputPath, outputPath, MaxLineLength: 1024);
        int verifyExitCode = await VerifyCommand.RunAsync(verifyOptions, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        Assert.Equal(0, verifyExitCode);
    }

    [Fact]
    [Trait("Case", "ET-03")]
    public async Task A_partitioned_merge_and_a_sequential_one_produce_the_same_file_from_the_same_input()
    {
        // The partitioned merge end to end, on a fixed input rather than a generated
        // one, so the run count and the merge shape are the same on every machine that
        // runs it. Three hundred lines over a hundred distinct keys: enough duplicates
        // that splitters routinely land on a key many lines share, and enough lines
        // that a 307-byte chunk produces a dozen or so runs -- all inside one pass at a
        // fan-in of 2048, which is the shape that gets partitioned.
        //
        // The two runs differ in nothing but the plan, and the plan differs in nothing
        // that should be able to change a single output byte: same input, same pipeline.
        // Byte-identity between them, and against the oracle, is the whole claim.
        const int maxLineLength = 64;
        const int partitionedParallelism = 71;
        const long partitionedBudget = 4_698_304;   // three merge workers; the pairing is
                                                     // derived longhand in the partitioned
                                                     // shape further down this file

        // 32, hardcoded rather than read off Program.AssumedMeanLineLength: that field
        // is `private`, and InternalsVisibleTo only reaches `internal` members, so
        // referencing it here would mean widening Program's accessibility for a test's
        // convenience. It is kept in step by hand.
        MemoryPlan partitionedPlan = MemoryBudget.Calculate(
            partitionedBudget, partitionedParallelism, maxLineLength, assumedMeanLineLength: 32);
        Assert.Equal(3, partitionedPlan.MergeParallelism);

        string inputPath = Path.Combine(_directory, "in.txt");
        StringBuilder text = new();
        for (int i = 0; i < 300; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(i * 37) % 100}. key{(i * 53) % 100:D3}\n");
        }

        byte[] input = Encoding.ASCII.GetBytes(text.ToString());
        File.WriteAllBytes(inputPath, input);
        byte[] expected = FileSorter.Tests.Properties.NaiveReferenceSort.Sort(input);

        byte[] partitioned = await SortAsync(inputPath, "partitioned", partitionedBudget, maxLineLength, partitionedParallelism);
        byte[] sequential = await SortAsync(inputPath, "sequential", 2L << 20, maxLineLength, parallelism: 2);

        Assert.Equal(expected, partitioned);
        Assert.Equal(expected, sequential);
        Assert.Equal(sequential, partitioned);
    }

    private async Task<byte[]> SortAsync(string inputPath, string name, long budget, int maxLineLength, int parallelism)
    {
        string outputPath = Path.Combine(_directory, $"out-{name}.txt");
        string tempDirectory = Path.Combine(_directory, $"temp-{name}");
        SorterOptions options = new(inputPath, outputPath, tempDirectory, budget, maxLineLength, parallelism, Pipeline.Channels);

        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        AssertNoLeftoverRunFiles(tempDirectory);
        return File.ReadAllBytes(outputPath);
    }

    [Fact]
    [Trait("Case", "ET-04")]
    public async Task Sorting_lines_ending_in_a_carriage_return_before_the_line_feed_sorts_byte_correctly_at_every_shape()
    {
        // The invariant: a run file's '\r' immediately before '\n' is content the
        // sorter carried in from an input line read as "...\r\r\n" (content "...\r"),
        // never a terminator's second half, so nothing downstream of phase one may
        // strip it a second time. Exercised at the four shapes that read run files
        // differently: a single run (a straight move, no RunCursor involved at all), a
        // multi-run single-pass sequential merge (N = 1, every run read through a
        // RunCursor window), a genuine multi-PASS sequential merge (N = 1, but
        // MergePlanner needs several rounds), and a partitioned merge (N >= 2, one
        // RunCursor per worker). Mixed terminators throughout -- bare '\n', ordinary
        // '\r\n', and content-'\r' before '\r\n' -- so this is not a one-line special
        // case.
        //
        // A budget sized by eye can collapse a shape into a different one: a chunk
        // larger than this whole file takes the single-run shortcut and never reaches
        // RunCursor at all, which is exactly the path that most needs covering. Each
        // shape below therefore states the plan it was computed against and asserts
        // that plan's figures plus the run count Program reports, so a constant move
        // that quietly changes a shape fails here rather than passing while testing
        // nothing.
        const int maxLineLength = 64;
        string inputPath = Path.Combine(_directory, "in.txt");

        byte[] input = Encoding.ASCII.GetBytes(BuildCrContentLines(60));
        File.WriteAllBytes(inputPath, input);
        byte[] expected = FileSorter.Tests.Properties.NaiveReferenceSort.Sort(input);

        // Single run: a budget comfortably larger than this file, one chunk, one
        // run, RunPlacement's move branch.
        byte[] singleRun = await SortAsync(inputPath, "single-run", 2L << 20, maxLineLength, parallelism: 2);

        // Multi-run, single-pass, N = 1: a high phase-one parallelism (30) squeezes
        // spillRoom -- and so the chunk -- down far enough that this 60-line file
        // produces several runs, while the budget stays far below the threshold for
        // admitting a second merge worker, so MergeParallelism stays 1.
        //   floorR = 66; floorBytesPerRunCursor = 2*66 + 2*32 = 196.
        //   spillRoom(30) = budget - 30*65_536; denom = 32*64+32 = 2_080.
        // The margin above MinimumViableBudget(30, 64, 32) is only 2,048 bytes, so the
        // chunk stays at 98 bytes rather than growing back toward one run, with
        // MergeFanIn 2048 -- far above any run count this file can produce, hence
        // single-pass.
        const int sequentialParallelism = 30;
        long sequentialBudget = MemoryBudget.MinimumViableBudget(sequentialParallelism, maxLineLength, assumedMeanLineLength: 32) + 2048;
        MemoryPlan sequentialPlan = MemoryBudget.Calculate(sequentialBudget, sequentialParallelism, maxLineLength, assumedMeanLineLength: 32);
        Assert.Equal(1, sequentialPlan.MergeParallelism);
        Assert.Equal(98, sequentialPlan.ChunkSize);
        (byte[] sequential, string sequentialStderr) =
            await SortCapturedAsync(inputPath, "sequential", sequentialBudget, maxLineLength, sequentialParallelism);
        int sequentialRunCount = ParseRunCount(sequentialStderr);
        Assert.Equal(23, sequentialRunCount); // observed; pins the shape rather than merely asserting >= 2
        Assert.True(sequentialRunCount >= 2, "the sequential shape must produce more than one run, or it is the single-run shortcut in disguise");
        Assert.Single(MergePlanner.Plan(sequentialRunCount, sequentialPlan.MergeFanIn));

        // Genuine multi-pass, N = 1: MinimumViableBudget's reference plan is built at
        // exactly MergeFanIn = 2, the bare minimum a viable budget can give, so the
        // minimum itself with no margin is the one configuration where any run count
        // above two forces several passes. At parallelism 4 that minimum's chunk
        // (spillRoom(4)*32/((4+2)*64+32)) is 60_531 bytes, so this shape needs a much
        // larger input than the other three: 30,000 lines of the same cycling shape,
        // comfortably several chunks' worth.
        const int multiPassParallelism = 4;
        long multiPassBudget = MemoryBudget.MinimumViableBudget(multiPassParallelism, maxLineLength, assumedMeanLineLength: 32);
        MemoryPlan multiPassPlan = MemoryBudget.Calculate(multiPassBudget, multiPassParallelism, maxLineLength, assumedMeanLineLength: 32);
        Assert.Equal(1, multiPassPlan.MergeParallelism);
        Assert.Equal(2, multiPassPlan.MergeFanIn); // MinMergeFanIn -- the whole point of using the bare minimum
        Assert.Equal(60_531, multiPassPlan.ChunkSize);

        string multiPassInputPath = Path.Combine(_directory, "in-multipass.txt");
        byte[] multiPassInput = Encoding.ASCII.GetBytes(BuildCrContentLines(30_000));
        File.WriteAllBytes(multiPassInputPath, multiPassInput);
        byte[] multiPassExpected = FileSorter.Tests.Properties.NaiveReferenceSort.Sort(multiPassInput);

        (byte[] multiPass, string multiPassStderr) =
            await SortCapturedAsync(multiPassInputPath, "multi-pass", multiPassBudget, maxLineLength, multiPassParallelism);
        int multiPassRunCount = ParseRunCount(multiPassStderr);
        Assert.Equal(16, multiPassRunCount); // observed
        int multiPassPasses = MergePlanner.Plan(multiPassRunCount, multiPassPlan.MergeFanIn).Count;
        Assert.Equal(4, multiPassPasses);
        Assert.True(multiPassPasses > 1, "this shape must force MergePlanner to run more than one pass, or it is no different from the single-pass shape above");

        // N >= 2: a high phase-one parallelism (71) again squeezes the chunk down far
        // enough for this file to produce several runs, at a budget that clears the
        // threshold for a third merge worker once the merge's own loser-tree and
        // partition-offset metadata is reserved:
        // room(3) = 4_698_304 - 3*1_048_576 - 2048*4*8 = 1_487_040;
        // adjustedRoom(3) (less 3*2048*44 of loser-tree metadata) = 1_216_704;
        // rawWindow(3) = floor(1_216_704*32/(3*2048*96)) = 66, at the floor exactly,
        // so three workers are admitted -- four are not (adjustedRoom(4) solves a
        // window of 2, far below the floor).
        const int partitionedParallelism = 71;
        const long partitionedBudget = 4_698_304;
        MemoryPlan partitionedPlan = MemoryBudget.Calculate(
            partitionedBudget, partitionedParallelism, maxLineLength, assumedMeanLineLength: 32);
        Assert.Equal(3, partitionedPlan.MergeParallelism);
        Assert.Equal(307, partitionedPlan.ChunkSize);
        (byte[] partitioned, string partitionedStderr) =
            await SortCapturedAsync(inputPath, "partitioned", partitionedBudget, maxLineLength, partitionedParallelism);
        int partitionedRunCount = ParseRunCount(partitionedStderr);
        Assert.Equal(7, partitionedRunCount); // observed
        Assert.True(partitionedRunCount >= 2);
        Assert.Single(MergePlanner.Plan(partitionedRunCount, partitionedPlan.MergeFanIn)); // single-pass: partitioning never applies otherwise
        Assert.Contains("merge parallelism 3:", partitionedStderr, StringComparison.Ordinal);

        Assert.Equal(expected, singleRun);
        Assert.Equal(expected, sequential);
        Assert.Equal(multiPassExpected, multiPass);
        Assert.Equal(expected, partitioned);

        foreach ((string name, string path) in new[]
                 {
                     ("single-run", inputPath), ("sequential", inputPath),
                     ("multi-pass", multiPassInputPath), ("partitioned", inputPath),
                 })
        {
            string outputPath = Path.Combine(_directory, $"out-{name}.txt");
            VerifyOptions verifyOptions = new(path, outputPath, MaxLineLength: maxLineLength);
            int verifyExitCode = await VerifyCommand.RunAsync(verifyOptions, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            Assert.Equal(0, verifyExitCode);
        }
    }

    // Builds `lines` lines cycling through a hundred distinct numbers and keys, one in
    // three ending its content in a '\r' (the "5. a\r\r\n" shape: the input rule strips
    // exactly one '\r' immediately before '\n', leaving the other as content) and the
    // rest split evenly between the two ordinary terminator conventions. Shared by
    // every merge shape above, so the larger multi-pass fixture exercises the identical
    // mix, just more of it.
    private static string BuildCrContentLines(int lines)
    {
        StringBuilder text = new();
        for (int i = 0; i < lines; i++)
        {
            int number = (i * 37) % 100;
            string key = $"key{(i * 53) % 100:D3}";
            string terminator = (i % 3) switch
            {
                0 => "\n",
                1 => "\r\n",
                _ => "\r\r\n",
            };

            text.Append(CultureInfo.InvariantCulture, $"{number}. {key}").Append(terminator);
        }

        return text.ToString();
    }

    // The same shape as SortAsync, plus the operator-facing stderr text, which is how a
    // fixture's real run count is pinned rather than assumed from its budget.
    // Console.Error is a process-wide static: the swap is scoped to the one awaited
    // call and restored in a `finally`, and every test class that drives Program sits
    // in the "Program" collection so no other sort can print its own run count into
    // this capture while the swap is in place.
    private async Task<(byte[] Output, string Stderr)> SortCapturedAsync(
        string inputPath, string name, long budget, int maxLineLength, int parallelism)
    {
        string outputPath = Path.Combine(_directory, $"out-{name}.txt");
        string tempDirectory = Path.Combine(_directory, $"temp-{name}");
        SorterOptions options = new(inputPath, outputPath, tempDirectory, budget, maxLineLength, parallelism, Pipeline.Channels);

        TextWriter originalError = Console.Error;
        StringWriter capturedError = new();
        int exitCode;
        Console.SetError(capturedError);
        try
        {
            exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Equal(0, exitCode);
        AssertNoLeftoverRunFiles(tempDirectory);
        return (await File.ReadAllBytesAsync(outputPath, TestContext.Current.CancellationToken), capturedError.ToString());
    }

    // Parses Program's "phase one produced N run(s) (...)" line. It is printed once
    // phase one finishes and is not gated by the periodic progress timer that the
    // merge-pass line is, so it is present however fast a small fixture sorts.
    private static int ParseRunCount(string stderr)
    {
        const string marker = "phase one produced ";
        int start = stderr.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected a \"{marker}\" line in stderr, got:\n{stderr}");
        start += marker.Length;
        int end = stderr.IndexOf(' ', start);
        return int.Parse(stderr[start..end], CultureInfo.InvariantCulture);
    }

    // The same Console.Error swap SortCapturedAsync uses, without that helper's own
    // assumption that the run succeeds: a caller here wants the exit code and the
    // stderr text for a run that may fail by design.
    private static async Task<(int ExitCode, string Stderr)> RunCapturedAsync(SorterOptions options)
    {
        TextWriter originalError = Console.Error;
        StringWriter capturedError = new();
        Console.SetError(capturedError);
        try
        {
            int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            return (exitCode, capturedError.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    [Trait("Case", "ET-05")]
    public async Task Sorting_the_sorters_own_output_again_loses_a_trailing_content_carriage_return()
    {
        // Sorting is not idempotent for a line whose content ends in '\r', and this
        // pins that limit rather than treating it as a defect: the input rule cannot
        // tell a content '\r' immediately before '\n' apart from a '\r\n' terminator's
        // second half by bytes alone.
        //
        // Input "1. a\r\r\n" parses -- strip exactly one '\r' immediately before '\n' --
        // to content "1. a\r"; the first, correct sort writes that content terminated
        // by a bare '\n', "1. a\r\n", keeping the content '\r' intact. Reading THAT
        // OUTPUT back in as a fresh sort's input hits the ordinary terminated-line
        // branch, which strips the '\r' before the '\n' as it would for any other
        // '\r\n' line, so the second sort's output is "1. a\n", one byte shorter.
        string firstInputPath = Path.Combine(_directory, "in1.txt");

        File.WriteAllText(firstInputPath, "1. a\r\r\n");

        byte[] firstOutput = await SortAsync(firstInputPath, "first", 2L << 20, maxLineLength: 1024, parallelism: 2);
        Assert.Equal("1. a\r\n"u8.ToArray(), firstOutput);

        // The first sort's output file (out-first.txt, per SortAsync's naming) is this
        // second call's input.
        string firstOutputPath = Path.Combine(_directory, "out-first.txt");
        byte[] secondOutput = await SortAsync(firstOutputPath, "second", 2L << 20, maxLineLength: 1024, parallelism: 2);
        Assert.Equal("1. a\n"u8.ToArray(), secondOutput);

        // Not merely "different", but different in exactly the one byte the limit
        // names: the second sort's output is the first's with its trailing content '\r'
        // removed.
        Assert.NotEqual(firstOutput, secondOutput);
        Assert.Equal(firstOutput.Length - 1, secondOutput.Length);
        Assert.Equal(firstOutput.AsSpan(0, secondOutput.Length - 1).ToArray(), secondOutput.AsSpan(0, secondOutput.Length - 1).ToArray());
        Assert.Equal((byte)'\r', firstOutput[^2]);
    }

    [Fact]
    public async Task Sorting_a_malformed_line_surfaces_MalformedLineException_naming_the_offending_line()
    {
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");

        // Line 2 has no '.', so LineParser.TryParse rejects it: the only grammar
        // violation that does not depend on maxLineLength.
        File.WriteAllText(inputPath, "1. Apple\nno separator here\n3. Banana\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);

        MalformedLineException thrown = await Assert.ThrowsAsync<MalformedLineException>(
            () => Program.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        Assert.Equal(2, thrown.LineNumber);
        Assert.Contains("no separator here", thrown.Message);
    }

    private static void WriteGeneratedInput(string path, long targetBytes, int seed)
    {
        GeneratorOptions options = new(path, targetBytes, seed, DuplicateRatio: 0.2);
        LineComposer composer = new(options);
        using FileStream output = new(path, FileMode.Create, FileAccess.Write);
        FileWriter.Write(output, composer, targetBytes);
    }

    // Checks both halves of the property independently of FileSorter.LineFormat: the
    // permutation half proves the same multiset, and the ordering half re-derives the
    // sort key from the raw line text rather than calling LineOrder.Compare, so a
    // comparator defect cannot hide behind the comparator being tested.
    private static void AssertIsSortedPermutationOfInput(string inputPath, string outputPath)
    {
        string[] inputLines = File.ReadAllLines(inputPath);
        string[] outputLines = File.ReadAllLines(outputPath);

        Assert.Equal(inputLines.Length, outputLines.Length);

        string[] expectedMultiset = [.. inputLines.OrderBy(line => line, StringComparer.Ordinal)];
        string[] actualMultiset = [.. outputLines.OrderBy(line => line, StringComparer.Ordinal)];
        Assert.Equal(expectedMultiset, actualMultiset);

        for (int i = 1; i < outputLines.Length; i++)
        {
            (long previousNumber, string previousString) = ParseIndependently(outputLines[i - 1]);
            (long currentNumber, string currentString) = ParseIndependently(outputLines[i]);

            int stringComparison = StringComparer.Ordinal.Compare(previousString, currentString);
            int comparison = stringComparison != 0 ? stringComparison : previousNumber.CompareTo(currentNumber);
            Assert.True(comparison <= 0, $"Output not sorted at line {i}: '{outputLines[i - 1]}' then '{outputLines[i]}'.");
        }
    }

    // Independently written oracle for a line's sort key: it deliberately calls nothing
    // from FileSorter.LineFormat, so it cannot share a defect with the production
    // parser or comparator.
    private static (long Number, string StringPart) ParseIndependently(string line)
    {
        int separator = line.IndexOf('.');
        string numberPart = line[..separator];
        int stringStart = separator + 1;
        if (stringStart < line.Length && line[stringStart] == ' ')
        {
            stringStart++;
        }

        return (long.Parse(numberPart, System.Globalization.CultureInfo.InvariantCulture), line[stringStart..]);
    }

    private static void AssertNoLeftoverRunFiles(string tempDirectory)
    {
        if (!Directory.Exists(tempDirectory))
        {
            return;
        }

        // Recursive: run files live inside this invocation's own private directory
        // beneath tempDirectory, not directly in it, so a non-recursive search would
        // miss a leak there entirely.
        string[] leftovers = Directory.GetFiles(tempDirectory, "run-*.tmp", SearchOption.AllDirectories);
        Assert.Empty(leftovers);

        // A leaked private directory is itself a leftover, even an empty one: nothing
        // beneath tempDirectory other than this run's own namespace should exist once
        // that run has finished.
        Assert.Empty(Directory.GetDirectories(tempDirectory));
    }

    [Fact]
    [Trait("Case", "ET-06")]
    public async Task An_unrelated_sentinel_named_like_a_run_file_survives_a_successful_sort()
    {
        // Run files must never share a namespace with anything a user could plausibly
        // have sitting next to the output, including a name matching the run-file
        // pattern this codebase's own runs take.
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = _directory;
        string sentinelPath = Path.Combine(_directory, "run-00000001.tmp");
        byte[] sentinelContent = "not a run file"u8.ToArray();
        File.WriteAllBytes(sentinelPath, sentinelContent);
        File.WriteAllText(inputPath, "2. Banana\n1. Apple\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("1. Apple\n2. Banana\n", File.ReadAllText(outputPath));
        Assert.Equal(sentinelContent, File.ReadAllBytes(sentinelPath));
    }

    [Fact]
    [Trait("Case", "ET-07")]
    public async Task An_output_named_like_a_run_file_is_produced_correctly()
    {
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "run-00000001.tmp");
        string tempDirectory = _directory;
        File.WriteAllText(inputPath, "2. Banana\n1. Apple\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outputPath));
        Assert.Equal("1. Apple\n2. Banana\n", File.ReadAllText(outputPath));
    }

    [Fact]
    [Trait("Case", "ET-08")]
    public async Task Two_concurrent_sorts_sharing_one_temp_parent_do_not_disturb_each_other()
    {
        string tempDirectory = Path.Combine(_directory, "shared-temp");

        string firstInput = Path.Combine(_directory, "in1.txt");
        string firstOutput = Path.Combine(_directory, "out1.txt");
        File.WriteAllText(firstInput, "2. Banana\n1. Apple\n");

        string secondInput = Path.Combine(_directory, "in2.txt");
        string secondOutput = Path.Combine(_directory, "out2.txt");
        File.WriteAllText(secondInput, "30. Cherry\n4. Date\n");

        SorterOptions firstOptions = new(firstInput, firstOutput, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        SorterOptions secondOptions = new(secondInput, secondOutput, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);

        // Nothing synchronises two different TemporaryRunSet instances against each
        // other, only the workers inside one, so two real, concurrently running
        // invocations are what actually exercises the private-directory-per-instance
        // guarantee -- two sequential ones would not.
        Task<int> firstRun = Program.RunAsync(firstOptions, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        Task<int> secondRun = Program.RunAsync(secondOptions, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        int[] exitCodes = await Task.WhenAll(firstRun, secondRun);

        Assert.All(exitCodes, code => Assert.Equal(0, code));
        Assert.Equal("1. Apple\n2. Banana\n", File.ReadAllText(firstOutput));
        Assert.Equal("30. Cherry\n4. Date\n", File.ReadAllText(secondOutput));

        // Both invocations shared tempDirectory, so this proves neither leaked a
        // private directory nor a run file into the other's namespace.
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    [Trait("Case", "ET-09")]
    public async Task Sorting_forces_a_multi_pass_merge_and_leaves_no_private_directory_behind_on_success()
    {
        // MinimumViableBudget's own reference plan pins MergeFanIn at MinMergeFanIn (2)
        // -- the bare minimum a viable budget can give -- so any run count above two
        // forces several merge passes rather than one, the same technique ET-04's own
        // multi-pass shape uses. The input needs to be large enough, at this budget's
        // tiny chunk size, to produce more than two runs.
        const int parallelism = 2;
        const int maxLineLength = 256;
        long budget = MemoryBudget.MinimumViableBudget(parallelism, maxLineLength, assumedMeanLineLength: 32);
        MemoryPlan plan = MemoryBudget.Calculate(budget, parallelism, maxLineLength, assumedMeanLineLength: 32);
        Assert.Equal(2, plan.MergeFanIn); // MinMergeFanIn -- the whole point of using the bare minimum

        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");
        WriteGeneratedInput(inputPath, targetBytes: 256 * 1024, seed: 5);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, budget, maxLineLength, parallelism, Pipeline.Channels);
        (int exitCode, string stderr) = await RunCapturedAsync(options);
        int runCount = ParseRunCount(stderr);
        int passes = MergePlanner.Plan(runCount, plan.MergeFanIn).Count;

        Assert.Equal(0, exitCode);
        Assert.True(runCount > 2, "the fixture must produce more runs than the fan-in, or a fan-in of 2 cannot force more than one pass");
        Assert.True(passes > 1, "this shape must force MergePlanner to run more than one pass, or it is no different from a single-pass merge");
        // The sorter leaves the parent alone and removes only its own private
        // subdirectory, so --temp survives success -- empty.
        Assert.True(Directory.Exists(tempDirectory));
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    [Trait("Case", "ET-10")]
    public async Task A_forced_failure_during_phase_one_still_removes_the_private_directory()
    {
        // MalformedLineException surfaces after some runs may already have been
        // spilled, so this is the case where the private directory genuinely held
        // files at the moment of failure, not merely an empty directory nobody wrote
        // into. TemporaryRunSet's cleanup runs from RunAsync's `using` declaration
        // regardless of how phase one exits.
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");
        File.WriteAllText(inputPath, "1. Apple\nno separator here\n3. Banana\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);

        await Assert.ThrowsAsync<MalformedLineException>(
            () => Program.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        // The sorter leaves the parent alone and removes only its own private
        // subdirectory, so --temp survives the failure -- empty.
        Assert.True(Directory.Exists(tempDirectory));
        AssertNoLeftoverRunFiles(tempDirectory);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    [Trait("Case", "ET-11")]
    public async Task An_empty_input_sort_reports_a_read_only_existing_destination_by_name_and_exits_3()
    {
        // Windows-only: FILE_ATTRIBUTE_READONLY blocks the replace File.Move performs
        // only there. The empty-input path never opens outputPath directly, so a
        // destination this run cannot replace must be left exactly as it was, and the
        // failure to replace it is reported by name at exit 3 -- the same code and
        // message shape an unwritable output directory already gets -- rather than
        // crashing with a stack trace.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A read-only attribute blocks File.Move's replace only on Windows.");

        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");
        File.WriteAllBytes(inputPath, []);
        byte[] previousContent = "previous output, not this run's"u8.ToArray();
        File.WriteAllBytes(outputPath, previousContent);
        File.SetAttributes(outputPath, FileAttributes.ReadOnly);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        try
        {
            (int exitCode, string stderr) = await RunCapturedAsync(options);

            Assert.Equal(3, exitCode);
            Assert.Contains(outputPath, stderr, StringComparison.Ordinal);
            Assert.Equal(previousContent, File.ReadAllBytes(outputPath));
            Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
        }
        finally
        {
            File.SetAttributes(outputPath, FileAttributes.Normal);
        }
    }

    [Fact]
    [Trait("Case", "ET-12")]
    public async Task A_single_run_sort_reports_a_read_only_existing_destination_by_name_and_exits_3()
    {
        // outputPath already existing forces tryMove's plain File.Move to fail (the
        // destination name is taken), which is what routes this through the
        // copy-then-move-into-place fallback in the first place; that fallback's own
        // final move is what a read-only destination then denies. Windows-only for
        // the reason given in the empty-input case above.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A read-only attribute blocks File.Move's replace only on Windows.");

        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");
        File.WriteAllText(inputPath, "1. Apple\n");
        byte[] previousContent = "previous output, not this run's"u8.ToArray();
        File.WriteAllBytes(outputPath, previousContent);
        File.SetAttributes(outputPath, FileAttributes.ReadOnly);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        try
        {
            (int exitCode, string stderr) = await RunCapturedAsync(options);

            Assert.Equal(3, exitCode);
            Assert.Contains(outputPath, stderr, StringComparison.Ordinal);
            Assert.Equal(previousContent, File.ReadAllBytes(outputPath));
            Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
        }
        finally
        {
            File.SetAttributes(outputPath, FileAttributes.Normal);
        }
    }

    [Fact]
    [Trait("Case", "ET-13")]
    public async Task A_successful_single_run_sort_still_replaces_an_existing_destination()
    {
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");
        File.WriteAllText(inputPath, "2. Banana\n1. Apple\n");
        File.WriteAllText(outputPath, "stale content from an earlier run\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("1. Apple\n2. Banana\n", File.ReadAllText(outputPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }

    [Fact]
    [Trait("Case", "ET-14")]
    public async Task An_empty_input_sort_reports_a_destination_that_is_a_directory_by_name_and_exits_3()
    {
        // The other shape File.Move's replace can fail on besides a read-only file: a
        // destination that is itself a directory. Portable, unlike ET-11: a rename
        // over an existing directory fails everywhere, not only under Windows sharing
        // rules.
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");
        File.WriteAllBytes(inputPath, []);
        Directory.CreateDirectory(outputPath);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        (int exitCode, string stderr) = await RunCapturedAsync(options);

        Assert.Equal(3, exitCode);
        Assert.Contains(outputPath, stderr, StringComparison.Ordinal);
        Assert.True(Directory.Exists(outputPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }

    [Fact]
    [Trait("Case", "ET-15")]
    public async Task A_single_run_sort_reports_a_destination_that_is_a_directory_by_name_and_exits_3()
    {
        string inputPath = Path.Combine(_directory, "in.txt");
        string outputPath = Path.Combine(_directory, "out.txt");
        string tempDirectory = Path.Combine(_directory, "temp");
        File.WriteAllText(inputPath, "1. Apple\n");
        Directory.CreateDirectory(outputPath);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        (int exitCode, string stderr) = await RunCapturedAsync(options);

        Assert.Equal(3, exitCode);
        Assert.Contains(outputPath, stderr, StringComparison.Ordinal);
        Assert.True(Directory.Exists(outputPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }
}
