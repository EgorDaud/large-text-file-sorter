using FileSorter.Merging;
using Xunit;

namespace FileSorter.Tests.Merging;

public sealed class MergePlannerTests
{
    [Fact]
    [Trait("Case", "MP-01")]
    public void Plans_one_pass_when_the_run_count_is_below_the_fan_in()
    {
        IReadOnlyList<MergePass> passes = MergePlanner.Plan(runCount: 3, fanIn: 5);

        MergePass pass = Assert.Single(passes);
        int[] group = Assert.Single(pass.Groups);
        Assert.Equal([0, 1, 2], group);
        Assert.Empty(pass.CarriedForward);
    }

    [Fact]
    [Trait("Case", "MP-02")]
    public void Plans_one_pass_when_the_run_count_equals_the_fan_in_exactly()
    {
        IReadOnlyList<MergePass> passes = MergePlanner.Plan(runCount: 5, fanIn: 5);

        MergePass pass = Assert.Single(passes);
        int[] group = Assert.Single(pass.Groups);
        Assert.Equal(5, group.Length);
        Assert.Empty(pass.CarriedForward);
    }

    [Fact]
    [Trait("Case", "MP-03")]
    public void Plans_two_passes_when_the_run_count_just_exceeds_the_fan_in()
    {
        // fanIn + 1 is exactly the shape that forces a group of size one under naive
        // division; the leftover run must be carried forward instead.
        IReadOnlyList<MergePass> passes = MergePlanner.Plan(runCount: 5, fanIn: 4);

        Assert.Equal(2, passes.Count);
        MergePass first = passes[0];
        Assert.All(first.Groups, group => Assert.True(group.Length is >= 2 and <= 4));
        Assert.DoesNotContain(first.Groups, group => group.Length == 1);
        Assert.Single(first.CarriedForward);
    }

    [Fact]
    [Trait("Case", "MP-04")]
    public void Plans_three_or_more_passes_for_a_large_run_count()
    {
        IReadOnlyList<MergePass> passes = MergePlanner.Plan(runCount: 100, fanIn: 4);

        Assert.True(passes.Count >= 3);

        int count = 100;
        foreach (MergePass pass in passes)
        {
            Assert.All(pass.Groups, group => Assert.True(group.Length <= 4));
            Assert.DoesNotContain(pass.Groups, group => group.Length == 1);

            int next = pass.Groups.Count + pass.CarriedForward.Count;
            Assert.True(next < count); // each pass strictly reduces the run count
            count = next;
        }

        Assert.Equal(1, count); // the final pass produces exactly one output
    }

    [Fact]
    [Trait("Case", "MP-05")]
    public void Handles_a_single_run_by_planning_no_passes_at_all()
    {
        IReadOnlyList<MergePass> passes = MergePlanner.Plan(runCount: 1, fanIn: 4);

        Assert.Empty(passes);
    }

    [Fact]
    [Trait("Case", "MP-06")]
    public void Handles_zero_runs_by_planning_no_passes()
    {
        IReadOnlyList<MergePass> passes = MergePlanner.Plan(runCount: 0, fanIn: 4);

        Assert.Empty(passes);
    }

    [Theory]
    [Trait("Case", "MP-07")]
    [InlineData(0)]
    [InlineData(1)]
    public void Rejects_a_fan_in_below_two(int fanIn)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MergePlanner.Plan(runCount: 10, fanIn));
    }

    [Theory]
    [Trait("Case", "MP-08")]
    [InlineData(4, 3)]   // remainder 1
    [InlineData(7, 2)]   // remainder 1
    [InlineData(10, 3)]  // remainder 1
    [InlineData(13, 4)]  // remainder 1
    [InlineData(101, 10)] // remainder 1, and large enough to span several passes
    public void Never_emits_a_group_of_size_one(int runCount, int fanIn)
    {
        Assert.Equal(1, runCount % fanIn);

        foreach (MergePass pass in MergePlanner.Plan(runCount, fanIn))
        {
            Assert.DoesNotContain(pass.Groups, group => group.Length == 1);
        }
    }

    [Fact]
    [Trait("Case", "MP-09")]
    public void Balances_group_sizes_within_a_pass_instead_of_leaving_one_nearly_empty()
    {
        IReadOnlyList<MergePass> passes = MergePlanner.Plan(runCount: 11, fanIn: 3);

        MergePass first = passes[0];
        int max = first.Groups.Max(group => group.Length);
        int min = first.Groups.Min(group => group.Length);
        Assert.True(max - min <= 1);
    }
}
