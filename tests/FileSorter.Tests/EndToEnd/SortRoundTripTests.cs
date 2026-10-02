using System.Globalization;
using System.Text;
using FileSorter.Cli;
using FileSorter.Merging;
using FileSorter.Planning;
using FileSorter.Tests.Support;
using FileSorter.Verification;
using Xunit;
using static FileSorter.Tests.EndToEnd.SortHarness;
using static FileSorter.Tests.Support.TestTimeouts;

namespace FileSorter.Tests.EndToEnd;

// Drives Program.RunAsync itself, the way the real binary's Main does, and checks that
// the output is the sorted input at every phase-two shape: empty, single run, sequential
// and partitioned merges, multi-pass, both pipelines, and the CR edge cases.
[Collection("Program")]
public sealed class SortRoundTripTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Sorting_an_empty_input_produces_an_empty_output_and_no_leftover_temp_files()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
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
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "1. Apple\n2. Banana\n3. Cherry\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, MaxLineLength: 20, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        AssertIsSortedPermutationOfInput(inputPath, outputPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sorting_forces_a_multi_pass_merge_when_the_memory_budget_is_small(bool useAkka)
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");

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
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        WriteGeneratedInput(inputPath, targetBytes: 512 * 1024, seed: 13);

        string akkaOutput = Path.Combine(_directory.Path, "akka-out.txt");
        string channelsOutput = Path.Combine(_directory.Path, "channels-out.txt");

        // 1,051,664 is the same budget the multi-pass test above derives longhand: just
        // clear of this triple's MinimumViableBudget of 1,050,120. This test needs only
        // a small, multi-pass-forcing budget, not a specific fan-in.
        SorterOptions akkaOptions = new(
            inputPath, akkaOutput, Path.Combine(_directory.Path, "akka-temp"), 1_051_664, 256, 2, Pipeline.Akka);
        SorterOptions channelsOptions = new(
            inputPath, channelsOutput, Path.Combine(_directory.Path, "channels-temp"), 1_051_664, 256, 2, Pipeline.Channels);

        int akkaExit = await Program.RunAsync(akkaOptions, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        int channelsExit = await Program.RunAsync(channelsOptions, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, akkaExit);
        Assert.Equal(0, channelsExit);
        Assert.Equal(File.ReadAllBytes(akkaOutput), File.ReadAllBytes(channelsOutput));
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
        string inputPath = Path.Combine(_directory.Path, "in.txt");

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
            string outputPath = Path.Combine(_directory.Path, $"out-{budget}.txt");
            string tempDirectory = Path.Combine(_directory.Path, $"temp-{budget}");
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
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
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

        string inputPath = Path.Combine(_directory.Path, "in.txt");
        StringBuilder text = new();
        for (int i = 0; i < 300; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(i * 37) % 100}. key{(i * 53) % 100:D3}\n");
        }

        byte[] input = Encoding.ASCII.GetBytes(text.ToString());
        File.WriteAllBytes(inputPath, input);
        byte[] expected = NaiveReferenceSort.Sort(input);

        byte[] partitioned = await SortAsync(inputPath, "partitioned", partitionedBudget, maxLineLength, partitionedParallelism);
        byte[] sequential = await SortAsync(inputPath, "sequential", 2L << 20, maxLineLength, parallelism: 2);

        Assert.Equal(expected, partitioned);
        Assert.Equal(expected, sequential);
        Assert.Equal(sequential, partitioned);
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
        string inputPath = Path.Combine(_directory.Path, "in.txt");

        byte[] input = Encoding.ASCII.GetBytes(BuildCrContentLines(60));
        File.WriteAllBytes(inputPath, input);
        byte[] expected = NaiveReferenceSort.Sort(input);

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

        // Genuine multi-pass, N = 1: The plan at the minimum viable budget has
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

        string multiPassInputPath = Path.Combine(_directory.Path, "in-multipass.txt");
        byte[] multiPassInput = Encoding.ASCII.GetBytes(BuildCrContentLines(30_000));
        File.WriteAllBytes(multiPassInputPath, multiPassInput);
        byte[] multiPassExpected = NaiveReferenceSort.Sort(multiPassInput);

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
            string outputPath = Path.Combine(_directory.Path, $"out-{name}.txt");
            VerifyOptions verifyOptions = new(path, outputPath, MaxLineLength: maxLineLength);
            int verifyExitCode = await VerifyCommand.RunAsync(verifyOptions, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            Assert.Equal(0, verifyExitCode);
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
        string firstInputPath = Path.Combine(_directory.Path, "in1.txt");

        File.WriteAllText(firstInputPath, "1. a\r\r\n");

        byte[] firstOutput = await SortAsync(firstInputPath, "first", 2L << 20, maxLineLength: 1024, parallelism: 2);
        Assert.Equal("1. a\r\n"u8.ToArray(), firstOutput);

        // The first sort's output file (out-first.txt, per SortAsync's naming) is this
        // second call's input.
        string firstOutputPath = Path.Combine(_directory.Path, "out-first.txt");
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

    private async Task<byte[]> SortAsync(string inputPath, string name, long budget, int maxLineLength, int parallelism)
    {
        string outputPath = Path.Combine(_directory.Path, $"out-{name}.txt");
        string tempDirectory = Path.Combine(_directory.Path, $"temp-{name}");
        SorterOptions options = new(inputPath, outputPath, tempDirectory, budget, maxLineLength, parallelism, Pipeline.Channels);

        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        AssertNoLeftoverRunFiles(tempDirectory);
        return File.ReadAllBytes(outputPath);
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
        string outputPath = Path.Combine(_directory.Path, $"out-{name}.txt");
        string tempDirectory = Path.Combine(_directory.Path, $"temp-{name}");
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
}
