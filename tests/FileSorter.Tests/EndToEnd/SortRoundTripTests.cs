using System.Globalization;
using System.Text;
using FileSorter.Cli;
using FileSorter.Merging;
using FileSorter.Planning;
using FileSorter.Tests.Support;
using Xunit;
using static FileSorter.Tests.EndToEnd.SortHarness;
using static FileSorter.Tests.Support.TestTimeouts;

namespace FileSorter.Tests.EndToEnd;

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
        int exitCode = await SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outputPath));
        Assert.Empty(File.ReadAllBytes(outputPath));
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    public async Task Sorting_with_a_max_line_below_the_internal_assumed_mean_does_not_crash()
    {
        // 20 is below AssumedMeanLineLength (32), which Calculate rejects unless RunAsync clamps it.
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "1. Apple\n2. Banana\n3. Cherry\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, MaxLineLength: 20, 2, Pipeline.Channels);
        int exitCode = await SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        AssertIsOracleSortOfInput(inputPath, outputPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sorting_forces_a_multi_pass_merge_when_the_memory_budget_is_small(bool useAkka)
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");

        WriteGeneratedInput(inputPath, targetBytes: 1024 * 1024, seed: 7);

        (int exitCode, string stderr) = await RunCapturedAsync(new(
            inputPath, outputPath, tempDirectory, MultiPassBudget, 256, Parallelism: 2, useAkka ? Pipeline.Akka : Pipeline.Channels));

        Assert.Equal(0, exitCode);
        AssertMultiPass(stderr, MemoryBudget.Calculate(MultiPassBudget, 2, 256, MemoryBudget.AssumedMeanLineLength));
        AssertIsOracleSortOfInput(inputPath, outputPath);
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    public async Task Both_pipelines_produce_byte_identical_output_for_a_multi_pass_run()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        WriteGeneratedInput(inputPath, targetBytes: 512 * 1024, seed: 13);

        string akkaOutput = Path.Combine(_directory.Path, "akka-out.txt");
        string channelsOutput = Path.Combine(_directory.Path, "channels-out.txt");

        (int akkaExit, string akkaStderr) = await RunCapturedAsync(new(
            inputPath, akkaOutput, Path.Combine(_directory.Path, "akka-temp"), MultiPassBudget, 256, 2, Pipeline.Akka));
        (int channelsExit, string channelsStderr) = await RunCapturedAsync(new(
            inputPath, channelsOutput, Path.Combine(_directory.Path, "channels-temp"), MultiPassBudget, 256, 2, Pipeline.Channels));

        Assert.Equal(0, akkaExit);
        Assert.Equal(0, channelsExit);
        MemoryPlan plan = MemoryBudget.Calculate(MultiPassBudget, 2, 256, MemoryBudget.AssumedMeanLineLength);
        AssertMultiPass(akkaStderr, plan);
        AssertMultiPass(channelsStderr, plan);
        Assert.Equal(File.ReadAllBytes(akkaOutput), File.ReadAllBytes(channelsOutput));
    }

    [Fact]
    [Trait("Case", "ET-01")]
    public async Task Sorting_a_crlf_file_of_maximum_length_lines_gives_identical_output_at_two_memory_budgets()
    {
        // Every line is exactly --max-line; two budgets land fill boundaries on different lines' CRs.
        const int maxLineLength = 8;
        string inputPath = Path.Combine(_directory.Path, "in.txt");

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

            int exitCode = await SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            byte[] output = File.ReadAllBytes(outputPath);
            Assert.DoesNotContain((byte)'\r', output);
            Assert.Equal(expected, output);
            previousOutput ??= output;
            Assert.Equal(previousOutput, output);
        }
    }

    [Fact]
    [Trait("Case", "ET-02")]
    public async Task Sorting_an_input_whose_final_unterminated_line_ends_in_a_bare_carriage_return_strips_it()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "2. Banana\n1. Apple\r");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
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
        // Fixed input with many duplicate keys, so partition splitters land on keys many lines share.
        const int maxLineLength = 64;
        const int partitionedParallelism = 71;
        const long partitionedBudget = 4_698_304;

        MemoryPlan partitionedPlan = MemoryBudget.Calculate(
            partitionedBudget, partitionedParallelism, maxLineLength, MemoryBudget.AssumedMeanLineLength);
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

        (byte[] partitioned, string partitionedStderr) =
            await SortCapturedAsync(inputPath, "partitioned", partitionedBudget, maxLineLength, partitionedParallelism);
        (byte[] sequential, string sequentialStderr) =
            await SortCapturedAsync(inputPath, "sequential", 2L << 20, maxLineLength, parallelism: 2);

        Assert.Contains("merge parallelism 3:", partitionedStderr, StringComparison.Ordinal);
        Assert.DoesNotContain("merge parallelism 3", sequentialStderr, StringComparison.Ordinal);
        Assert.Equal(expected, partitioned);
        Assert.Equal(expected, sequential);
        Assert.Equal(sequential, partitioned);
    }

    [Fact]
    [Trait("Case", "ET-04")]
    public async Task Sorting_lines_ending_in_a_carriage_return_before_the_line_feed_sorts_byte_correctly_at_every_shape()
    {
        // A run file's '\r' before '\n' is content from a "...\r\r\n" input line; nothing after phase one may strip it.
        const int maxLineLength = 64;
        string inputPath = Path.Combine(_directory.Path, "in.txt");

        byte[] input = Encoding.ASCII.GetBytes(BuildCrContentLines(60));
        File.WriteAllBytes(inputPath, input);
        byte[] expected = NaiveReferenceSort.Sort(input);

        byte[] singleRun = await SortAsync(inputPath, "single-run", 2L << 20, maxLineLength, parallelism: 2);

        // Parallelism 30 shrinks the chunk so 60 lines spill many runs; a 2048-byte margin keeps one merge worker.
        const int sequentialParallelism = 30;
        long sequentialBudget = MemoryBudget.MinimumViableBudget(sequentialParallelism, maxLineLength, MemoryBudget.AssumedMeanLineLength) + 2048;
        MemoryPlan sequentialPlan = MemoryBudget.Calculate(sequentialBudget, sequentialParallelism, maxLineLength, MemoryBudget.AssumedMeanLineLength);
        Assert.Equal(1, sequentialPlan.MergeParallelism);
        Assert.Equal(98, sequentialPlan.ChunkSize);
        (byte[] sequential, string sequentialStderr) =
            await SortCapturedAsync(inputPath, "sequential", sequentialBudget, maxLineLength, sequentialParallelism);
        int sequentialRunCount = ParseRunCount(sequentialStderr);
        Assert.Equal(23, sequentialRunCount); // observed
        Assert.True(sequentialRunCount >= 2, "the sequential shape must produce more than one run, or it is the single-run shortcut in disguise");
        Assert.Single(MergePlanner.Plan(sequentialRunCount, sequentialPlan.MergeFanIn));

        // chunk = spillRoom(4)*32/((4+2)*64+32) = 60_531, hence the 30,000-line input.
        const int multiPassParallelism = 4;
        long multiPassBudget = MemoryBudget.MinimumViableBudget(multiPassParallelism, maxLineLength, MemoryBudget.AssumedMeanLineLength);
        MemoryPlan multiPassPlan = MemoryBudget.Calculate(multiPassBudget, multiPassParallelism, maxLineLength, MemoryBudget.AssumedMeanLineLength);
        Assert.Equal(1, multiPassPlan.MergeParallelism);
        Assert.Equal(2, multiPassPlan.MergeFanIn);
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

        // room(3) = 4_698_304 - 3*1_048_576 - 2048*4*8 - 3*2048*44 = 1_216_704;
        // window(3) = 1_216_704*32/(3*2048*96) = 66, exactly the floor, so three merge workers fit.
        const int partitionedParallelism = 71;
        const long partitionedBudget = 4_698_304;
        MemoryPlan partitionedPlan = MemoryBudget.Calculate(
            partitionedBudget, partitionedParallelism, maxLineLength, MemoryBudget.AssumedMeanLineLength);
        Assert.Equal(3, partitionedPlan.MergeParallelism);
        Assert.Equal(307, partitionedPlan.ChunkSize);
        (byte[] partitioned, string partitionedStderr) =
            await SortCapturedAsync(inputPath, "partitioned", partitionedBudget, maxLineLength, partitionedParallelism);
        int partitionedRunCount = ParseRunCount(partitionedStderr);
        Assert.Equal(7, partitionedRunCount); // observed
        Assert.True(partitionedRunCount >= 2);
        Assert.Single(MergePlanner.Plan(partitionedRunCount, partitionedPlan.MergeFanIn)); // partitioning applies only to a single pass
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
        // A known limit, not a defect: input bytes cannot tell a content '\r' before '\n' from a CRLF terminator.
        string firstInputPath = Path.Combine(_directory.Path, "in1.txt");

        File.WriteAllText(firstInputPath, "1. a\r\r\n");

        byte[] firstOutput = await SortAsync(firstInputPath, "first", 2L << 20, maxLineLength: 1024, parallelism: 2);
        Assert.Equal("1. a\r\n"u8.ToArray(), firstOutput);

        string firstOutputPath = Path.Combine(_directory.Path, "out-first.txt");
        byte[] secondOutput = await SortAsync(firstOutputPath, "second", 2L << 20, maxLineLength: 1024, parallelism: 2);
        Assert.Equal("1. a\n"u8.ToArray(), secondOutput);

        Assert.NotEqual(firstOutput, secondOutput);
        Assert.Equal(firstOutput.Length - 1, secondOutput.Length);
        Assert.Equal(firstOutput.AsSpan(0, secondOutput.Length - 1).ToArray(), secondOutput.AsSpan(0, secondOutput.Length - 1).ToArray());
        Assert.Equal((byte)'\r', firstOutput[^2]);
    }

    // Just above the minimum viable budget at --max-line 256 and parallelism 2, so the fan-in is small.
    private const long MultiPassBudget = 1_051_664;

    private static void AssertMultiPass(string stderr, MemoryPlan plan)
    {
        int runCount = ParseRunCount(stderr);
        Assert.True(runCount > plan.MergeFanIn, $"{runCount} run(s) at a fan-in of {plan.MergeFanIn} is a single-pass merge.");
        Assert.True(MergePlanner.Plan(runCount, plan.MergeFanIn).Count > 1);
    }

    private async Task<byte[]> SortAsync(string inputPath, string name, long budget, int maxLineLength, int parallelism)
    {
        string outputPath = Path.Combine(_directory.Path, $"out-{name}.txt");
        string tempDirectory = Path.Combine(_directory.Path, $"temp-{name}");
        SorterOptions options = new(inputPath, outputPath, tempDirectory, budget, maxLineLength, parallelism, Pipeline.Channels);

        int exitCode = await SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        AssertNoLeftoverRunFiles(tempDirectory);
        return File.ReadAllBytes(outputPath);
    }

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

    private async Task<(byte[] Output, string Stderr)> SortCapturedAsync(
        string inputPath, string name, long budget, int maxLineLength, int parallelism)
    {
        string outputPath = Path.Combine(_directory.Path, $"out-{name}.txt");
        string tempDirectory = Path.Combine(_directory.Path, $"temp-{name}");
        SorterOptions options = new(inputPath, outputPath, tempDirectory, budget, maxLineLength, parallelism, Pipeline.Channels);

        (int exitCode, string stderr) = await RunCapturedAsync(options);

        Assert.Equal(0, exitCode);
        AssertNoLeftoverRunFiles(tempDirectory);
        return (await File.ReadAllBytesAsync(outputPath, TestContext.Current.CancellationToken), stderr);
    }
}
