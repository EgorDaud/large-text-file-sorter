using CsCheck;
using FileSorter.Planning;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.Properties;

[Collection("Program")]
public sealed class ByteIdentityPropertyTests
{
    private const int MaxLineLength = 256;

    private const long LargeBudgetBytes = 4L * 1024 * 1024;

    // High parallelism shrinks the chunk enough to make several runs; at low parallelism the budget floor yields one run.
    private const int SmallBudgetParallelism = 20;

    private static readonly long SmallBudgetBytes =
        MemoryBudget.MinimumViableBudget(SmallBudgetParallelism, MaxLineLength, MemoryBudget.AssumedMeanLineLength) + 2048;

    private static readonly MemoryPlan SmallBudgetPlan =
        MemoryBudget.Calculate(SmallBudgetBytes, SmallBudgetParallelism, MaxLineLength, MemoryBudget.AssumedMeanLineLength);

    [Fact]
    [Trait("Case", "PB-01")]
    public async Task Sorting_a_generated_file_matches_the_independent_oracle_byte_for_byte()
    {
        await Gen.Select(LineEntryGen.Entries, LineEntryGen.FinalTermination).SampleAsync(async t =>
        {
            (List<(long Number, string StringPart)> entries, (RandomLineFileGen.Terminator Terminator, bool TrailingCarriageReturn) finalTermination) = t;
            byte[] input = LineEntryGen.BuildInput(entries, finalTermination);
            byte[] expected = NaiveReferenceSort.Sort(input);

            byte[] actual = await PropertyHarness.RunSorterAsync(
                input, LargeBudgetBytes, MaxLineLength, TestContext.Current.CancellationToken);

            Assert.Equal(expected, actual);
        }, iter: 150);
    }

    [Fact]
    [Trait("Case", "PB-02")]
    public async Task Sorting_the_same_file_at_two_memory_budgets_both_match_the_oracle()
    {
        // Leading-zero and explicit-sign ties must order the same however the input is chunked.
        Assert.Equal(304, SmallBudgetPlan.ChunkSize);
        Assert.Equal(1, SmallBudgetPlan.MergeParallelism);

        await Gen.Select(LineEntryGen.Entries, LineEntryGen.FinalTermination).SampleAsync(async t =>
        {
            (List<(long Number, string StringPart)> entries, (RandomLineFileGen.Terminator Terminator, bool TrailingCarriageReturn) finalTermination) = t;
            byte[] input = LineEntryGen.BuildInput(entries, finalTermination);
            byte[] expected = NaiveReferenceSort.Sort(input);
            CancellationToken ct = TestContext.Current.CancellationToken;

            byte[] atSmallBudget = await PropertyHarness.RunSorterAsync(
                input, SmallBudgetBytes, MaxLineLength, ct, SmallBudgetParallelism);
            byte[] atLargeBudget = await PropertyHarness.RunSorterAsync(input, LargeBudgetBytes, MaxLineLength, ct);

            Assert.Equal(expected, atSmallBudget);
            Assert.Equal(expected, atLargeBudget);
        }, iter: 60);
    }

    [Fact]
    [Trait("Case", "PB-13")]
    public async Task Sorting_the_same_file_through_a_partitioned_merge_matches_the_oracle_too()
    {
        // Tuned so the budget just admits three merge workers over several single-pass runs.
        const int maxLineLength = 64;
        const int parallelism = 71;
        const long budgetBytes = 4_698_304;

        MemoryPlan plan = MemoryBudget.Calculate(budgetBytes, parallelism, maxLineLength, MemoryBudget.AssumedMeanLineLength);
        Assert.Equal(3, plan.MergeParallelism);
        Assert.Equal(307, plan.ChunkSize);

        await Gen.Select(LineEntryGen.Entries, LineEntryGen.FinalTermination).SampleAsync(async t =>
        {
            (List<(long Number, string StringPart)> entries, (RandomLineFileGen.Terminator Terminator, bool TrailingCarriageReturn) finalTermination) = t;
            byte[] input = LineEntryGen.BuildInput(entries, finalTermination);
            byte[] expected = NaiveReferenceSort.Sort(input);

            byte[] actual = await PropertyHarness.RunSorterAsync(
                input, budgetBytes, maxLineLength, TestContext.Current.CancellationToken, parallelism);

            Assert.Equal(expected, actual);
        }, iter: 60);
    }
}
