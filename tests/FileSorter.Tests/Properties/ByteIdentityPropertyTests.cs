using CsCheck;
using FileSorter.Startup;
using Xunit;

namespace FileSorter.Tests.Properties;

/// <summary>
/// Runs the real sorter over a generated file and asserts the output is byte-identical
/// to <see cref="NaiveReferenceSort"/>, an oracle that shares no code with the sorter.
/// A partial oracle -- "the output is non-decreasing" -- would accept a sorter that
/// drops or duplicates lines; byte-identity is a complete specification of the output
/// as a value and rejects all of those at once.
/// </summary>
[Collection("Program")]
public sealed class ByteIdentityPropertyTests
{
    // MaxLineLength: generous headroom over the longest line these generators can
    // produce (long.MinValue's 20 characters, ". ", 16 characters of string part, and
    // the zero-padded twin's two extra digits -- under 48 bytes at the extreme).
    private const int MaxLineLength = 256;

    // The same value Program.AssumedMeanFor clamps to, so the minimum-budget figure
    // below is the one Program.RunAsync computes. Program's own constant is private and
    // InternalsVisibleTo does not reach it, so this copy has to be kept in step by hand.
    private const int AssumedMeanLineLength = 32;

    // Comfortably a single chunk for these few-kilobyte generated files, so phase two
    // takes the single-run placement path rather than any merge.
    private const long LargeBudgetBytes = 4L * 1024 * 1024;

    // A minimum budget at a low parallelism does not produce small chunks: at this
    // MaxLineLength it is dominated by phase two's own floor (OutputBufferSize, about
    // 1 MiB) and the resulting chunk is far larger than any file these generators
    // produce, so the whole input would land in one run and the small budget would
    // exercise nothing. A high phase-one parallelism squeezes spillRoom, and with it
    // the chunk, down to where several runs are actually generated.
    private const int SmallBudgetParallelism = 20;

    private static readonly long SmallBudgetBytes =
        MemoryBudget.MinimumViableBudget(SmallBudgetParallelism, MaxLineLength, AssumedMeanLineLength) + 2048;

    // The plan is asserted below rather than assumed, so a constant that regrows this
    // chunk back toward "one run regardless of input" fails the test instead of quietly
    // testing nothing. A 304-byte chunk is smaller than most of what LineEntryGen
    // produces, forcing several runs out of a few-kilobyte file, while the fan-in stays
    // comfortably above any run count this generator can reach, so the merge is
    // single-pass.
    private static readonly MemoryPlan SmallBudgetPlan =
        MemoryBudget.Calculate(SmallBudgetBytes, SmallBudgetParallelism, MaxLineLength, AssumedMeanLineLength);

    [Fact]
    [Trait("Case", "PB-01")]
    public async Task Sorting_a_generated_file_matches_the_independent_oracle_byte_for_byte()
    {
        // The final line's own termination is drawn alongside the entries -- ordinary
        // '\n', explicit '\r\n', none at all, or a bare trailing '\r' -- so this, the
        // suite's highest-value property, is what actually exercises the reader's
        // end-of-file rule against the oracle rather than only ever seeing '\n'.
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
        // Without a correct third comparison level, a leading-zero or explicit-sign
        // tie's relative order depends on which chunk it lands in, which depends on the
        // budget. Running the identical input at a small budget (many small chunks) and
        // a large one (a single chunk) and requiring both to match the same
        // byte-identical oracle is what exercises that: asserting only that the two
        // runs agree with each other would pass if both were wrong the same way.
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
        // A partitioned merge exercised through the whole program, where the plan, the
        // run count, the pass count and the partition all have to agree with each other
        // rather than being handed to one type by a test.
        //
        // Reaching more than one merge worker through Program.RunAsync is a narrow
        // configuration by design: MemoryBudget only raises the worker count once the
        // budget affords MaxMergeFanIn cursors for each of them, which normally means a
        // budget far larger than a few-kilobyte file's chunk. The way through is a small
        // --max-line (64, so a cursor at the floor window costs 196 bytes rather than
        // tens of kilobytes) and a large parallelism (71, which divides the chunk down
        // to 307 bytes). At OutputBufferSize = 1 MiB:
        //   floorR = 66; floorBytesPerRunCursor = 2*66+2*32 = 196.
        //   room(4) = 4_698_304 - 4*1_048_576 - 2048*5*8 = 422_080 (n > 1 reserves the
        //   partition's own offset table too); adjustedRoom(4), less 4*2048*44 of
        //   loser-tree metadata, = 61_632; rawWindow(4) =
        //   floor(61_632*32/(4*2048*96)) = 2, far below the floor -- four workers are
        //   not admitted.
        //   room(3) = 4_698_304 - 3*1_048_576 - 2048*4*8 = 1_487_040;
        //   adjustedRoom(3) = 1_487_040 - 3*2048*44 = 1_216_704; rawWindow(3) =
        //   floor(1_216_704*32/(3*2048*96)) = floor(38_934_528/589_824) = 66 --
        //   exactly at the floor, so three workers ARE admitted.
        //   chunkSize = floor((4_698_304 - 71*65_536)*32 / (73*64+32)) =
        //               floor(45_248*32 / 4_704) = floor(1_447_936/4_704) = 307.
        // At 307 bytes a chunk, a file of a few hundred bytes produces several runs, all
        // inside one pass at a fan-in of 2048, which is the single-pass shape the merge
        // partitions. The assertion below keeps the configuration honest: without it a
        // constant could move, the merge could fall back to one worker, and the property
        // would still pass while testing nothing new.
        const int maxLineLength = 64;
        const int parallelism = 71;
        const long budgetBytes = 4_698_304;

        MemoryPlan plan = MemoryBudget.Calculate(budgetBytes, parallelism, maxLineLength, AssumedMeanLineLength);
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
