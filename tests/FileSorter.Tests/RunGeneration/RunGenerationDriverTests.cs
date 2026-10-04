using FileSorter.Infrastructure;
using FileSorter.Planning;
using FileSorter.RunGeneration;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.RunGeneration;

public sealed class RunGenerationDriverTests : IDisposable
{
    private const int MaxLineLength = 63;

    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    [Trait("Case", "SL-06")]
    public async Task The_strategy_receives_a_reader_over_the_input_a_working_spill_and_the_plans_parallelism()
    {
        string inputPath = Path.Combine(_directory.Path, "input.txt");
        await File.WriteAllTextAsync(inputPath, "2. Banana\n1. Apple\n", TestContext.Current.CancellationToken);
        using TemporaryRunSet runs = new(_directory.Path);
        MemoryPlan plan = TestPlans.Merge(fanIn: 2) with { Parallelism = 3 };
        int seenParallelism = 0;

        IReadOnlyList<string> paths = await RunGenerationDriver.GenerateRunsAsync(
            inputPath,
            MaxLineLength,
            plan,
            runs,
            async (reader, spill, parallelism, ct) =>
            {
                seenParallelism = parallelism;
                Chunk chunk = await reader.ReadNextAsync(ct) ?? throw new InvalidOperationException("No chunk.");
                return [await spill(chunk, ct)];
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, seenParallelism);
        string path = Assert.Single(paths);
        Assert.Equal("1. Apple\n2. Banana\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Case", "SL-07")]
    public async Task The_input_is_closed_when_the_strategy_throws()
    {
        string inputPath = Path.Combine(_directory.Path, "input.txt");
        await File.WriteAllTextAsync(inputPath, "1. Apple\n", TestContext.Current.CancellationToken);
        using TemporaryRunSet runs = new(_directory.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunGenerationDriver.GenerateRunsAsync(
            inputPath,
            MaxLineLength,
            TestPlans.Merge(fanIn: 2),
            runs,
            (reader, spill, parallelism, ct) => throw new InvalidOperationException("strategy failed"),
            TestContext.Current.CancellationToken));

        // FileShare.None fails while the driver's read handle is still open.
        using FileStream exclusive = new(inputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
