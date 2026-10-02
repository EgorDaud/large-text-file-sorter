using FileSorter.Merging;
using FileSorter.Planning;
using Xunit;

namespace FileSorter.Tests.Planning;

// Expected figures are worked by hand, not re-derived from Calculate's own expression.
// OutputBuffer 1_048_576, SpillBuffer 65_536, descriptor 32, loser-tree slot 44, window floor maxLine + 2.
public sealed class MemoryBudgetTests
{
    [Fact]
    [Trait("Case", "MB-01")]
    public void Produces_a_viable_plan_for_a_normal_budget()
    {
        // chunk = (2_097_152 - 4*65_536) * 64 / (6*96 + 64) = 183_500
        // fan-in = (2_097_152 - 1_048_576) / (2*1026 + 16*32 + 44) = 402
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 2_097_152, parallelism: 4, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(183_500, plan.ChunkSize);
        Assert.Equal(2867, plan.DescriptorCapacity);
        Assert.Equal(4, plan.Parallelism);
        Assert.Equal(402, plan.MergeFanIn);
        Assert.Equal(1026, plan.ReadAheadBufferSize);
        Assert.Equal(16, plan.ReadAheadDescriptorCapacity);
        Assert.Equal(1_048_576, plan.OutputBufferSize);
        Assert.Equal(65_536, plan.SpillBufferSize);
        Assert.Equal(2_097_108, plan.WorstCasePhaseOneBytes);
        Assert.Equal(17_688, plan.MergeMetadataBytes);
        Assert.Equal(2_096_992, plan.WorstCasePhaseTwoBytes);
        Assert.True(plan.WorstCasePhaseOneBytes <= 2_097_152);
        Assert.True(plan.WorstCasePhaseTwoBytes <= 2_097_152);
        Assert.True(plan.MergeFanIn >= 2);
    }

    [Fact]
    [Trait("Case", "MB-10")]
    public void Grows_the_read_ahead_window_once_the_fan_in_ceiling_leaves_the_budget_idle()
    {
        // window = (4 GiB - 8*1_048_576 - 2048*9*8 - 8*2048*44) * 64 / (8*2048*160) = 104_631
        const long fourGiB = 4L * 1024 * 1024 * 1024;
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: fourGiB, parallelism: 16, maxLineLength: 65_536, assumedMeanLineLength: 64);

        Assert.Equal(2048, plan.MergeFanIn);
        Assert.Equal(8, plan.MergeParallelism);
        Assert.Equal(104_631, plan.ReadAheadBufferSize);
        Assert.Equal(1_634, plan.ReadAheadDescriptorCapacity);
        Assert.Equal(868_352, plan.MergeMetadataBytes);
        Assert.Equal(4_294_492_160, plan.WorstCasePhaseTwoBytes);
        Assert.True(plan.WorstCasePhaseTwoBytes <= fourGiB);

        Assert.True(plan.ReadAheadBufferSize > 65_536 + 1);
        Assert.True(plan.MergeFanIn > 64);
    }

    [Fact]
    [Trait("Case", "MB-11")]
    public void Clamps_the_merge_worker_count_at_the_measured_ceiling_rather_than_at_the_window_floor()
    {
        // window = (4 GiB - 8*1_048_576 - 2048*9*8 - 8*2048*44) * 32 / (8*2048*96) = 87_193
        const long fourGiB = 4L * 1024 * 1024 * 1024;
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: fourGiB, parallelism: 16, maxLineLength: 65_536, assumedMeanLineLength: 32);

        Assert.Equal(8, plan.MergeParallelism);
        Assert.Equal(87_193, plan.ReadAheadBufferSize);
        Assert.Equal(2_724, plan.ReadAheadDescriptorCapacity);
        Assert.Equal(2048, plan.MergeFanIn);
        Assert.Equal(868_352, plan.MergeMetadataBytes);
        Assert.Equal(4_294_557_696, plan.WorstCasePhaseTwoBytes);
        Assert.True(plan.WorstCasePhaseTwoBytes <= fourGiB);

        MemoryPlan atThirtyTwo = MemoryBudget.Calculate(
            budgetBytes: fourGiB, parallelism: 32, maxLineLength: 65_536, assumedMeanLineLength: 32);
        Assert.Equal(8, atThirtyTwo.MergeParallelism);
    }

    [Fact]
    [Trait("Case", "MB-11")]
    public void Clamps_the_merge_worker_count_by_the_window_floor_when_the_budget_binds_first()
    {
        // window(3) = 87_192 clears the 65_538 floor; window(4) does not.
        const long budget = 3L * 512 * 1024 * 1024;
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: budget, parallelism: 16, maxLineLength: 65_536, assumedMeanLineLength: 32);

        Assert.Equal(3, plan.MergeParallelism);
        Assert.True(plan.ReadAheadBufferSize >= 65_538);
        Assert.True(plan.WorstCasePhaseTwoBytes <= budget);
        Assert.True(plan.MergeFanIn >= 2);
    }

    [Fact]
    [Trait("Case", "MB-11")]
    public void Never_gives_the_merge_more_workers_than_the_parallelism_it_was_asked_for()
    {
        const long fourGiB = 4L * 1024 * 1024 * 1024;
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: fourGiB, parallelism: 3, maxLineLength: 65_536, assumedMeanLineLength: 32);

        Assert.Equal(3, plan.MergeParallelism);

        MemoryPlan single = MemoryBudget.Calculate(
            budgetBytes: fourGiB, parallelism: 1, maxLineLength: 65_536, assumedMeanLineLength: 32);
        Assert.Equal(1, single.MergeParallelism);
    }

    [Fact]
    [Trait("Case", "MB-12")]
    public void Falls_back_to_one_merge_worker_at_a_budget_too_small_to_grow_the_window()
    {
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 2_097_152, parallelism: 4, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(1, plan.MergeParallelism);
        Assert.Equal(2_096_992, plan.WorstCasePhaseTwoBytes);

        // minimum = 2*(2*1026 + 16*32) + 1_048_576 + 2*44 = 1_053_792
        long minimumAtP4 = MemoryBudget.MinimumViableBudget(4, 1024, 64);
        Assert.Equal(1_053_792, minimumAtP4);
        MemoryPlan atP4Minimum = MemoryBudget.Calculate(
            budgetBytes: minimumAtP4, parallelism: 4, maxLineLength: 1024, assumedMeanLineLength: 64);
        Assert.Equal(1, atP4Minimum.MergeParallelism);
        Assert.Equal(2, atP4Minimum.MergeFanIn);
        Assert.Equal(1_026, atP4Minimum.ReadAheadBufferSize);
        Assert.Equal(88, atP4Minimum.MergeMetadataBytes);
        Assert.Equal(1_053_792, atP4Minimum.WorstCasePhaseTwoBytes);
        Assert.True(atP4Minimum.WorstCasePhaseTwoBytes <= minimumAtP4);

        // At parallelism 48 the spill buffers raise the minimum enough to afford two merge workers:
        // window(2) = (3_152_495 - 2*1_048_576 - 2048*3*8 - 2*2048*44) * 32 / (2*2048*96) = 67
        long minimumAtP48 = MemoryBudget.MinimumViableBudget(48, 64, 32);
        Assert.Equal(3_152_495, minimumAtP48);
        MemoryPlan atP48Minimum = MemoryBudget.Calculate(
            budgetBytes: minimumAtP48, parallelism: 48, maxLineLength: 64, assumedMeanLineLength: 32);
        Assert.Equal(2, atP48Minimum.MergeParallelism);
        Assert.Equal(67, atP48Minimum.ReadAheadBufferSize);
        Assert.Equal(2048, atP48Minimum.MergeFanIn);
        Assert.Equal(229_376, atP48Minimum.MergeMetadataBytes);
        Assert.Equal(3_137_536, atP48Minimum.WorstCasePhaseTwoBytes);
        Assert.True(atP48Minimum.WorstCasePhaseTwoBytes <= minimumAtP48);
    }

    [Fact]
    [Trait("Case", "MB-13")]
    public void Keeps_phase_two_inside_the_budget_and_the_worker_count_monotone_across_the_range_that_adds_workers()
    {
        const int parallelism = 16;
        const int maxLineLength = 65_536;
        const int assumedMeanLineLength = 32;
        const long first = 300L * 1024 * 1024;
        const long last = 4L * 1024 * 1024 * 1024 + (256L * 1024 * 1024);
        const long step = 23L * 1024 * 1024;

        int previousWorkers = 0;
        int highestWorkers = 0;
        for (long budget = first; budget <= last; budget += step)
        {
            MemoryPlan plan = MemoryBudget.Calculate(budget, parallelism, maxLineLength, assumedMeanLineLength);

            Assert.True(
                plan.WorstCasePhaseTwoBytes <= budget,
                $"Phase two's worst case ({plan.WorstCasePhaseTwoBytes} bytes) exceeded the budget ({budget}) at {plan.MergeParallelism} workers.");
            Assert.True(plan.MergeFanIn >= 2);
            Assert.True(plan.MergeParallelism >= 1);
            Assert.True(plan.MergeParallelism <= parallelism);
            Assert.True(
                plan.ReadAheadBufferSize >= maxLineLength + 2,
                $"The per-worker window ({plan.ReadAheadBufferSize}) fell below its floor at {plan.MergeParallelism} workers.");
            Assert.True(
                plan.MergeParallelism >= previousWorkers,
                $"The merge worker count fell from {previousWorkers} to {plan.MergeParallelism} as the budget rose to {budget}.");

            previousWorkers = plan.MergeParallelism;
            highestWorkers = Math.Max(highestWorkers, plan.MergeParallelism);
        }

        Assert.Equal(8, highestWorkers);
    }

    [Fact]
    [Trait("Case", "MB-02")]
    public void Fails_clearly_when_the_budget_is_one_byte_below_the_minimum_viable_budget()
    {
        // minimum = 2*(2*1026 + 16*32) + 1_048_576 + 2*44 = 1_053_792
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryBudget.Calculate(
                budgetBytes: 1_053_791, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64));

        Assert.Equal("budgetBytes", ex.ParamName);
        Assert.Contains("1053792", ex.Message);
        Assert.Contains("1053791", ex.Message);
        Assert.Contains("at parallelism 1.", ex.Message);
    }

    [Fact]
    [Trait("Case", "MB-02")]
    public void Succeeds_at_exactly_the_minimum_viable_budget_with_a_fan_in_of_exactly_two()
    {
        // chunk = (1_053_792 - 65_536) * 64 / (3*96 + 64) = 179_682
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 1_053_792, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(2, plan.MergeFanIn);
        Assert.True(plan.ChunkSize > 1024);
        Assert.Equal(179_682, plan.ChunkSize);
        Assert.Equal(1_053_736, plan.WorstCasePhaseOneBytes);
        Assert.Equal(88, plan.MergeMetadataBytes);
        Assert.Equal(1_053_792, plan.WorstCasePhaseTwoBytes);
        Assert.True(plan.WorstCasePhaseOneBytes <= 1_053_792);
        Assert.True(plan.WorstCasePhaseTwoBytes <= 1_053_792);
    }

    [Theory]
    [Trait("Case", "MB-02")]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 1)]
    public void Rejects_a_max_line_length_too_close_to_int_MaxValue_for_this_classs_own_floors_to_fit(int maxLineLength)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryBudget.MinimumViableBudget(parallelism: 1, maxLineLength, assumedMeanLineLength: 64));

        Assert.Equal("maxLineLength", ex.ParamName);
    }

    [Fact]
    [Trait("Case", "MB-02")]
    public void Reports_no_minimum_viable_budget_for_a_max_line_length_just_below_the_overflow_guard()
    {
        bool found = MemoryBudget.TryFindMinimumViableBudget(
            parallelism: 1, maxLineLength: int.MaxValue - 20, assumedMeanLineLength: 64, out long minimum);

        Assert.False(found);
        Assert.Equal(0, minimum);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryBudget.MinimumViableBudget(parallelism: 1, maxLineLength: int.MaxValue - 20, assumedMeanLineLength: 64));
    }

    [Theory]
    [Trait("Case", "MB-02")]
    [InlineData(0, 1024, 64)]
    [InlineData(4, 0, 64)]
    public void Rejects_non_positive_parallelism_or_max_line_length(int parallelism, int maxLineLength, int assumedMean)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryBudget.Calculate(1_000_000, parallelism, maxLineLength, assumedMean));
    }

    [Theory]
    [Trait("Case", "MB-02")]
    [InlineData(0)]
    [InlineData(2000)]
    public void Rejects_an_assumed_mean_line_length_that_is_non_positive_or_exceeds_the_maximum(int assumedMean)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryBudget.Calculate(1_000_000, parallelism: 4, maxLineLength: 1024, assumedMean));
    }

    [Fact]
    [Trait("Case", "MB-03")]
    public void Handles_a_degree_of_parallelism_of_one()
    {
        // chunk = (1_100_000 - 65_536) * 64 / (3*96 + 64) = 188_084
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 1_100_000, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(1, plan.Parallelism);
        Assert.Equal(188_084, plan.ChunkSize);
        Assert.Equal(2938, plan.DescriptorCapacity);
        Assert.Equal(1_099_920, plan.WorstCasePhaseOneBytes);
        Assert.True(plan.WorstCasePhaseOneBytes <= 1_100_000);
    }

    [Fact]
    [Trait("Case", "MB-04")]
    public void Fails_clearly_when_the_requested_parallelism_is_more_than_the_budget_can_support()
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryBudget.Calculate(
                budgetBytes: 500_000, parallelism: 1000, maxLineLength: 1024, assumedMeanLineLength: 64));

        Assert.Contains("1000", ex.Message, StringComparison.Ordinal);
        Assert.Contains("500000", ex.Message, StringComparison.Ordinal);
        Assert.Contains("minimum viable budget", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Case", "MB-04")]
    public void Honours_the_requested_parallelism_exactly_when_the_budget_supports_it()
    {
        // chunk = (1_100_000 - 4*65_536) * 64 / (6*96 + 64) = 83_785
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 1_100_000, parallelism: 4, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(4, plan.Parallelism);
        Assert.Equal(83_785, plan.ChunkSize);
        Assert.Equal(1309, plan.DescriptorCapacity);
        Assert.Equal(1_099_967, plan.WorstCasePhaseOneBytes);
        Assert.True(plan.WorstCasePhaseOneBytes <= 1_100_000);

        // A chunk must exceed the maxLine + 2 reserve; a run cursor's window need only reach it.
        Assert.True(plan.ChunkSize > 1024 + 2);
        Assert.True(plan.ReadAheadBufferSize > 1024 + 1);
    }

    [Fact]
    [Trait("Case", "MB-05")]
    public void Accounts_for_the_output_buffer_when_sizing_the_fan_in_not_only_the_read_ahead_cost()
    {
        // budget = 1_048_576 + 3*(2564 + 44); ignoring the output buffer and loser tree gives 1_056_400 / 2564 = 411
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 1_056_400, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(3, plan.MergeFanIn);
        Assert.NotEqual(411, plan.MergeFanIn);
        Assert.Equal(132, plan.MergeMetadataBytes);
        Assert.Equal(1_056_400, plan.WorstCasePhaseTwoBytes);
        Assert.True(plan.WorstCasePhaseTwoBytes <= 1_056_400);
    }

    [Fact]
    [Trait("Case", "MB-06")]
    public void Accounts_for_the_descriptor_array_overhead_at_a_short_assumed_mean_line_length()
    {
        // chunk = (1_150_000 - 65_536) * 8 / (3*40 + 8) = 67_779
        // ignoring descriptors: 4 * chunk <= 1_150_000, so chunk <= 287_500
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 1_150_000, parallelism: 1, maxLineLength: 64, assumedMeanLineLength: 8);

        Assert.Equal(67_779, plan.ChunkSize);
        Assert.True(plan.ChunkSize < 287_500);
        Assert.Equal(1_149_964, plan.WorstCasePhaseOneBytes);
        Assert.True(plan.WorstCasePhaseOneBytes <= 1_150_000);
    }

    [Fact]
    [Trait("Case", "MB-07")]
    public void Responds_monotonically_to_a_larger_budget_at_the_same_parallelism()
    {
        // chunk = (budget - 65_536) * 64 / 352; fan-in = (budget - 1_048_576) / 2608
        MemoryPlan smaller = MemoryBudget.Calculate(
            budgetBytes: 1_150_000, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64);
        MemoryPlan larger = MemoryBudget.Calculate(
            budgetBytes: 2_300_000, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64);

        Assert.Equal(197_175, smaller.ChunkSize);
        Assert.Equal(38, smaller.MergeFanIn);
        Assert.Equal(406_266, larger.ChunkSize);
        Assert.Equal(479, larger.MergeFanIn);

        Assert.True(larger.ChunkSize >= smaller.ChunkSize);
        Assert.True(larger.MergeFanIn >= smaller.MergeFanIn);
        Assert.True(larger.ReadAheadBufferSize >= smaller.ReadAheadBufferSize);
    }

    [Fact]
    [Trait("Case", "MB-07")]
    public void A_larger_budget_never_yields_a_smaller_chunk_across_the_range_that_used_to_cliff()
    {
        // The window leaves its 1026 floor at budget 6_306_816, inside this range.
        const int parallelism = 8;
        const int maxLineLength = 1024;
        const int assumedMeanLineLength = 64;
        const long first = 4_200_000;
        const long last = 7_200_000;
        const long step = 97;

        MemoryPlan previous = MemoryBudget.Calculate(first, parallelism, maxLineLength, assumedMeanLineLength);
        for (long budget = first + step; budget <= last; budget += step)
        {
            MemoryPlan current = MemoryBudget.Calculate(budget, parallelism, maxLineLength, assumedMeanLineLength);

            Assert.True(
                current.ChunkSize >= previous.ChunkSize,
                $"Chunk size fell from {previous.ChunkSize} to {current.ChunkSize} as the budget rose to {budget}.");
            Assert.True(
                current.MergeFanIn >= previous.MergeFanIn,
                $"Fan-in fell from {previous.MergeFanIn} to {current.MergeFanIn} as the budget rose to {budget}.");
            Assert.True(
                current.ReadAheadBufferSize >= previous.ReadAheadBufferSize,
                $"Read-ahead window fell from {previous.ReadAheadBufferSize} to {current.ReadAheadBufferSize} as the budget rose to {budget}.");
            Assert.Equal(parallelism, current.Parallelism);

            Assert.True(
                current.WorstCasePhaseTwoBytes <= budget,
                $"Phase two's worst case ({current.WorstCasePhaseTwoBytes} bytes) exceeded the budget ({budget}).");
            Assert.True(current.MergeFanIn >= 2);

            previous = current;
        }
    }

    [Fact]
    [Trait("Case", "MB-14")]
    public void Never_plans_a_chunk_larger_than_the_largest_allocatable_array_at_a_huge_budget()
    {
        const long budget = 64L * 1024 * 1024 * 1024;
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: budget, parallelism: 8, maxLineLength: 65_536, assumedMeanLineLength: 32);

        Assert.Equal(Array.MaxLength, plan.ChunkSize);
        Assert.True(plan.WorstCasePhaseOneBytes <= budget);
        Assert.True(plan.WorstCasePhaseTwoBytes <= budget);
    }

    [Fact]
    [Trait("Case", "MB-14")]
    public void Clamps_the_chunk_to_the_largest_array_exactly_where_the_unclamped_chunk_would_exceed_it()
    {
        // rawChunk = (budget - 8*65_536) / 21, so it reaches Array.MaxLength at 21*2_147_483_591 + 524_288
        Assert.Equal(2_147_483_591, Array.MaxLength);
        const long firstClampedBudget = 45_097_679_699;

        MemoryPlan atBoundary = MemoryBudget.Calculate(
            budgetBytes: firstClampedBudget, parallelism: 8, maxLineLength: 65_536, assumedMeanLineLength: 32);
        MemoryPlan belowBoundary = MemoryBudget.Calculate(
            budgetBytes: firstClampedBudget - 1, parallelism: 8, maxLineLength: 65_536, assumedMeanLineLength: 32);

        MemoryPlan aboveBoundary = MemoryBudget.Calculate(
            budgetBytes: firstClampedBudget + 21 * 10, parallelism: 8, maxLineLength: 65_536, assumedMeanLineLength: 32);
        Assert.Equal(Array.MaxLength, aboveBoundary.ChunkSize);

        Assert.Equal(2_147_483_591, atBoundary.ChunkSize);
        Assert.Equal(2_147_483_590, belowBoundary.ChunkSize);
        Assert.True(atBoundary.WorstCasePhaseOneBytes <= firstClampedBudget);
        Assert.True(belowBoundary.WorstCasePhaseOneBytes <= firstClampedBudget - 1);
    }

    [Theory]
    [Trait("Case", "MB-15")]
    [InlineData(1L << 60)]
    [InlineData(long.MaxValue)]
    public void Plans_a_budget_far_above_what_the_arithmetic_can_multiply_without_overflow(long budget)
    {
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: budget, parallelism: 8, maxLineLength: 65_536, assumedMeanLineLength: 32);

        Assert.Equal(Array.MaxLength, plan.ChunkSize);
        Assert.True(plan.MergeFanIn >= 2);
        Assert.True(plan.WorstCasePhaseOneBytes <= budget);
        Assert.True(plan.WorstCasePhaseTwoBytes <= budget);
    }

    [Fact]
    [Trait("Case", "MB-08")]
    public void Produces_identical_plans_from_identical_inputs_across_repeated_calls()
    {
        MemoryPlan first = MemoryBudget.Calculate(2_097_152, 4, 1024, 64);

        for (int i = 0; i < 50; i++)
        {
            MemoryPlan repeat = MemoryBudget.Calculate(2_097_152, 4, 1024, 64);
            Assert.Equal(first, repeat);
        }
    }

    [Fact]
    [Trait("Case", "MB-09")]
    public void Passes_its_fan_in_to_the_real_planner_which_accepts_it_and_terminates_in_one_run()
    {
        // fan-in = (2_000_000 - 1_048_576) / 2608 = 364
        MemoryPlan plan = MemoryBudget.Calculate(
            budgetBytes: 2_000_000, parallelism: 4, maxLineLength: 1024, assumedMeanLineLength: 64);
        Assert.Equal(364, plan.MergeFanIn);

        IReadOnlyList<MergePass> passes = MergePlanner.Plan(runCount: 500, fanIn: plan.MergeFanIn);

        int count = 500;
        foreach (MergePass pass in passes)
        {
            int next = pass.Groups.Count + pass.CarriedForward.Count;
            Assert.True(next < count);
            count = next;
        }

        Assert.Equal(1, count);
    }

    [Fact]
    [Trait("Case", "MB-09")]
    public void Never_returns_a_plan_whose_fan_in_the_real_planner_would_reject()
    {
        // fan-in = (1_051_140 - 1_048_576) / 2608 = 0
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryBudget.Calculate(
                budgetBytes: 1_051_140, parallelism: 1, maxLineLength: 1024, assumedMeanLineLength: 64));
    }
}
