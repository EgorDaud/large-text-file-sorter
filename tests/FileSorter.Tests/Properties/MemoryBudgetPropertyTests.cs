using CsCheck;
using FileSorter.Startup;
using Xunit;

namespace FileSorter.Tests.Properties;

/// <summary>
/// Property coverage for <see cref="MemoryBudget.Calculate"/> over the whole of its
/// input space. MemoryBudgetTests pins hand-derived figures at chosen points; this fuzzes
/// the space between them, where a budget solver built from floored integer divisions and
/// a worker-count search can go wrong without any single curated point noticing: a plan
/// that overspends its own budget, a viability boundary that is not where
/// <see cref="MemoryBudget.MinimumViableBudget"/> says it is, or a larger budget that buys
/// a smaller chunk or fan-in. The contract here is deliberately independent of how
/// Calculate reaches its answer, so it holds across a rewrite of the solver.
/// </summary>
public sealed class MemoryBudgetPropertyTests
{
    // Program.AssumedMeanLineLength, which is private; AssumedMeanFor clamps it to
    // --max-line so a tiny line limit never produces an invalid pair.
    private const int ShippedAssumedMeanLineLength = 32;

    private static readonly int MaxLineLengthCeiling = (int)CommandLine.MaxLineLengthCeiling;

    private const long MaxBudget = 256L << 30;

    private static readonly Gen<int> Parallelism = Gen.Int[1, 64];

    // Line limits and absolute budgets are drawn log-uniformly: each has to be tried at
    // every order of magnitude, not mostly at the largest one, which a plain uniform range
    // would do. A budget is either absolute, from 1 KiB to 256 GiB, or one placed relative to the
    // minimum viable budget for the drawn pair, from just under it to about 90 times it.
    // The absolute draw alone would almost never land near the boundary for a large
    // --max-line, whose minimum is itself in the gigabytes.
    private static readonly Gen<(bool Relative, double Position)> BudgetShape = Gen.Select(Gen.Bool, Gen.Double[0, 1]);

    private static readonly Gen<(int Parallelism, int MaxLineLength, long BudgetBytes)> Scenario =
        ScenarioUpTo(MaxLineLengthCeiling);

    [Fact]
    [Trait("Case", "PB-20")]
    public void Every_plan_Calculate_returns_fits_its_budget_and_meets_the_planner_minimums()
    {
        Scenario.Sample(scenario =>
        {
            (int parallelism, int maxLineLength, long budget) = scenario;

            if (!TryCalculate(budget, parallelism, maxLineLength, out MemoryPlan plan))
            {
                return;
            }

            string inputs = Describe(budget, parallelism, maxLineLength);
            Assert.True(plan.WorstCasePhaseOneBytes <= budget, $"phase one over budget: {inputs}");
            Assert.True(plan.WorstCasePhaseTwoBytes <= budget, $"phase two over budget: {inputs}");
            Assert.True(plan.ChunkSize <= Array.MaxLength, $"chunk above Array.MaxLength: {inputs}");
            Assert.True((long)plan.ChunkSize > (long)maxLineLength + 2, $"chunk cannot hold a line plus its reserve: {inputs}");
            Assert.True(plan.MergeFanIn >= 2, $"fan-in below two: {inputs}");
            Assert.InRange(plan.MergeParallelism, 1, parallelism);
            Assert.True(
                plan.ReadAheadBufferSize >= (long)maxLineLength + 2, $"read-ahead window below its floor: {inputs}");
        }, iter: 20_000);
    }

    [Fact]
    [Trait("Case", "PB-21")]
    public void Viability_starts_exactly_at_the_minimum_viable_budget_and_stays_viable_above_it()
    {
        Scenario.Sample(scenario =>
        {
            (int parallelism, int maxLineLength, long budget) = scenario;
            long minimum = MemoryBudget.MinimumViableBudget(parallelism, maxLineLength, AssumedMeanFor(maxLineLength));

            // MinimumViableBudget is a binary search over TryCalculate, which assumes
            // viability is monotone, so this checks both the edge and a drawn budget on
            // either side of it, rather than only that the edge is where it should be.
            Assert.True(
                TryCalculate(minimum, parallelism, maxLineLength, out _),
                $"the minimum viable budget {minimum} is not viable: {Describe(minimum, parallelism, maxLineLength)}");
            Assert.False(
                TryCalculate(minimum - 1, parallelism, maxLineLength, out _),
                $"one byte under the minimum is viable: {Describe(minimum - 1, parallelism, maxLineLength)}");
            Assert.Equal(
                budget >= minimum,
                TryCalculate(budget, parallelism, maxLineLength, out _));
        }, iter: 20_000);
    }

    [Fact]
    [Trait("Case", "PB-22")]
    public void Minimum_viable_budget_is_viable_for_the_largest_max_lines_the_command_line_accepts()
    {
        // The sampled draws almost never land on the last few line limits, where the
        // chunk must still exceed maxLine + 2 yet fit one array. A ceiling above
        // Array.MaxLength - 3 once made MinimumViableBudget name a figure at which
        // Calculate still threw; the CLI ceiling now excludes that.
        for (int parallelism = 1; parallelism <= 64; parallelism++)
        {
            foreach (int maxLineLength in new[] { MaxLineLengthCeiling - 1, MaxLineLengthCeiling })
            {
                long minimum = MemoryBudget.MinimumViableBudget(parallelism, maxLineLength, ShippedAssumedMeanLineLength);

                Assert.True(
                    TryCalculate(minimum, parallelism, maxLineLength, out MemoryPlan plan),
                    $"the minimum viable budget is not viable: {Describe(minimum, parallelism, maxLineLength)}");
                Assert.True(
                    plan.WorstCasePhaseOneBytes <= minimum && plan.WorstCasePhaseTwoBytes <= minimum,
                    $"the plan at the minimum exceeds it: {Describe(minimum, parallelism, maxLineLength)}");
                Assert.False(
                    TryCalculate(minimum - 1, parallelism, maxLineLength, out _),
                    $"one byte under the minimum is viable: {Describe(minimum - 1, parallelism, maxLineLength)}");
            }

            // And the ceiling is the largest such limit: one more is unviable at any budget.
            Assert.False(
                TryCalculate(1L << 40, parallelism, MaxLineLengthCeiling + 1, out _),
                $"a line limit above the ceiling is viable: {Describe(1L << 40, parallelism, MaxLineLengthCeiling + 1)}");
        }
    }

    [Fact]
    [Trait("Case", "PB-23")]
    public void A_larger_budget_never_yields_a_smaller_chunk_or_fan_in()
    {
        // The second budget is the first plus a log-uniform step from a byte to about
        // 16 GiB: small steps probe the cliffs a pair of unrelated budgets would step
        // over, large ones probe the regimes between them.
        Gen.Select(Scenario, Gen.Double[0, 34]).Sample(t =>
        {
            ((int parallelism, int maxLineLength, long smaller), double stepExponent) = t;
            long larger = Math.Min(MaxBudget, smaller + (long)Math.Pow(2, stepExponent));

            if (!TryCalculate(smaller, parallelism, maxLineLength, out MemoryPlan smallerPlan))
            {
                return;
            }

            Assert.True(
                TryCalculate(larger, parallelism, maxLineLength, out MemoryPlan largerPlan),
                $"a larger budget became non-viable: {Describe(smaller, parallelism, maxLineLength)} -> {larger}");
            string inputs = $"{Describe(smaller, parallelism, maxLineLength)} -> {larger}";
            Assert.True(largerPlan.ChunkSize >= smallerPlan.ChunkSize, $"chunk shrank: {inputs}");
            Assert.True(largerPlan.MergeFanIn >= smallerPlan.MergeFanIn, $"fan-in shrank: {inputs}");
        }, iter: 20_000);
    }

    private static Gen<(int Parallelism, int MaxLineLength, long BudgetBytes)> ScenarioUpTo(int maxLineLengthLimit) =>
        Gen.Select(
            Parallelism,
            Gen.Double[0, 31].Select(exponent => (int)Math.Min(maxLineLengthLimit, Math.Max(1, Math.Pow(2, exponent)))),
            BudgetShape).Select(t => (t.Item1, t.Item2, BudgetFor(t.Item1, t.Item2, t.Item3)));

    private static long BudgetFor(int parallelism, int maxLineLength, (bool Relative, double Position) shape)
    {
        if (!shape.Relative)
        {
            return (long)Math.Pow(2, 10 + shape.Position * 28);
        }

        long minimum = MemoryBudget.MinimumViableBudget(parallelism, maxLineLength, AssumedMeanFor(maxLineLength));
        return Math.Min(MaxBudget, (long)(minimum * Math.Pow(2, -0.5 + shape.Position * 7)));
    }

    private static int AssumedMeanFor(int maxLineLength) => Math.Min(ShippedAssumedMeanLineLength, maxLineLength);

    // Only the budget argument is the budget's own failure: any other exception is a
    // defect in Calculate and has to fail the property rather than count as "rejected".
    private static bool TryCalculate(long budget, int parallelism, int maxLineLength, out MemoryPlan plan)
    {
        try
        {
            plan = MemoryBudget.Calculate(budget, parallelism, maxLineLength, AssumedMeanFor(maxLineLength));
            return true;
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "budgetBytes")
        {
            plan = default;
            return false;
        }
    }

    private static string Describe(long budget, int parallelism, int maxLineLength) =>
        $"budget={budget}, parallelism={parallelism}, maxLine={maxLineLength}";
}
