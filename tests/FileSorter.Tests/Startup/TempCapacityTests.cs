using FileSorter.Startup;
using Xunit;

namespace FileSorter.Tests.Startup;

public sealed class TempCapacityTests
{
    [Fact]
    [Trait("Case", "SC-01")]
    public void Proceeds_when_free_space_comfortably_exceeds_the_requirement()
    {
        CapacityDecision decision = TempCapacity.Evaluate(inputSizeBytes: 1_000_000, freeBytes: 10_000_000);

        Assert.Equal(CapacityOutcome.Sufficient, decision.Outcome);
        Assert.Equal(2_000_000, decision.RequiredBytes);
        Assert.Equal(10_000_000, decision.AvailableBytes);
    }

    [Fact]
    [Trait("Case", "SC-02")]
    public void Fails_when_free_space_is_below_the_requirement()
    {
        CapacityDecision decision = TempCapacity.Evaluate(inputSizeBytes: 1_000_000, freeBytes: 1_999_999);

        Assert.Equal(CapacityOutcome.Insufficient, decision.Outcome);
        Assert.Equal(2_000_000, decision.RequiredBytes);
        Assert.Equal(1_999_999, decision.AvailableBytes);
    }

    [Fact]
    [Trait("Case", "SC-03")]
    public void Treats_free_space_exactly_equal_to_the_requirement_as_sufficient()
    {
        // The comparison is deliberately inclusive: the multiplier is already a
        // conservative upper bound, so a volume that meets the requirement exactly is a
        // run the bound itself says will fit.
        CapacityDecision decision = TempCapacity.Evaluate(inputSizeBytes: 1_000_000, freeBytes: 2_000_000);

        Assert.Equal(CapacityOutcome.Sufficient, decision.Outcome);
        Assert.Equal(2_000_000, decision.RequiredBytes);
        Assert.Equal(2_000_000, decision.AvailableBytes);
    }

    [Fact]
    [Trait("Case", "SC-04")]
    public void Proceeds_for_an_empty_input_with_a_zero_requirement()
    {
        CapacityDecision decision = TempCapacity.Evaluate(inputSizeBytes: 0, freeBytes: 0);

        Assert.Equal(CapacityOutcome.Sufficient, decision.Outcome);
        Assert.Equal(0, decision.RequiredBytes);
    }

    [Fact]
    [Trait("Case", "SC-05")]
    public void Reports_unknown_with_a_negative_sentinel_rather_than_zero_when_free_space_is_unreported()
    {
        CapacityDecision decision = TempCapacity.Evaluate(inputSizeBytes: 1_000_000, freeBytes: null);

        Assert.Equal(CapacityOutcome.Unknown, decision.Outcome);
        Assert.Equal(2_000_000, decision.RequiredBytes); // still known, independent of free space
        Assert.Equal(-1, decision.AvailableBytes);        // never 0 -- 0 would read as "0 bytes available"
    }

    [Theory]
    [Trait("Case", "SC-06")]
    [InlineData(0, 0)]
    [InlineData(1_000, 2_000)]
    [InlineData(1_000_000, 2_000_000)]
    [InlineData(1_000_000_000_000, 2_000_000_000_000)] // 1 TB input, several orders of magnitude up from the others
    public void Computes_the_requirement_as_exactly_twice_the_input_size_across_several_magnitudes(
        long inputSizeBytes, long expectedRequiredBytes)
    {
        CapacityDecision decision = TempCapacity.Evaluate(inputSizeBytes, freeBytes: long.MaxValue);

        Assert.Equal(expectedRequiredBytes, decision.RequiredBytes);
        Assert.Equal(TempCapacity.RequirementMultiplier, 2); // the multiplier is 2, named rather than reasserted inline
    }

    [Fact]
    [Trait("Case", "SC-06")]
    public void Clamps_the_requirement_instead_of_overflowing_for_an_input_near_the_long_range()
    {
        // inputSizeBytes * 2 wraps into a negative number well before inputSizeBytes
        // reaches long.MaxValue, so the requirement clamps instead.
        CapacityDecision decision = TempCapacity.Evaluate(inputSizeBytes: long.MaxValue, freeBytes: long.MaxValue);

        Assert.Equal(long.MaxValue, decision.RequiredBytes);
        Assert.True(decision.RequiredBytes >= 0); // never wrapped negative
    }

    [Fact]
    [Trait("Case", "SC-07")]
    public void EvaluateSameVolume_requires_twice_the_input_plus_the_capacity_margin()
    {
        // When --temp defaults to the output's own directory, both live on one volume,
        // and the combined bound is Evaluate's twice-the-input bound plus a small
        // margin -- not a second, larger multiplier stacked on top of it.
        const long inputSizeBytes = 1_000_000;
        long required = (inputSizeBytes * TempCapacity.RequirementMultiplier) + TempCapacity.CapacityMarginBytes;

        CapacityDecision atRequirement = TempCapacity.EvaluateSameVolume(inputSizeBytes, required);
        CapacityDecision oneByteShort = TempCapacity.EvaluateSameVolume(inputSizeBytes, required - 1);

        Assert.Equal(CapacityOutcome.Sufficient, atRequirement.Outcome);
        Assert.Equal(required, atRequirement.RequiredBytes);
        Assert.Equal(CapacityOutcome.Insufficient, oneByteShort.Outcome);
    }

    [Fact]
    [Trait("Case", "SC-08")]
    public void EvaluateOutputVolume_requires_roughly_one_input_size_plus_the_capacity_margin()
    {
        // The output file never exceeds the input's size, because run files are
        // '\n'-normalised, so the output volume's bound uses a multiplier of one plus
        // the same margin. That is strictly less than the temp volume requires once the
        // input is large enough for the fixed margin not to dominate -- hence the 1 GB
        // input here rather than the smaller ones the other cases use.
        const long inputSizeBytes = 1_000_000_000;
        long required = inputSizeBytes + TempCapacity.CapacityMarginBytes;

        CapacityDecision atRequirement = TempCapacity.EvaluateOutputVolume(inputSizeBytes, required);
        CapacityDecision oneByteShort = TempCapacity.EvaluateOutputVolume(inputSizeBytes, required - 1);

        Assert.Equal(CapacityOutcome.Sufficient, atRequirement.Outcome);
        Assert.Equal(required, atRequirement.RequiredBytes);
        Assert.Equal(CapacityOutcome.Insufficient, oneByteShort.Outcome);
        Assert.True(atRequirement.RequiredBytes < inputSizeBytes * TempCapacity.RequirementMultiplier);
    }

    [Fact]
    [Trait("Case", "SC-08")]
    public void EvaluateOutputVolume_reports_unknown_with_the_negative_sentinel_when_unreported()
    {
        CapacityDecision decision = TempCapacity.EvaluateOutputVolume(inputSizeBytes: 1_000_000, freeBytes: null);

        Assert.Equal(CapacityOutcome.Unknown, decision.Outcome);
        Assert.Equal(1_000_000 + TempCapacity.CapacityMarginBytes, decision.RequiredBytes);
        Assert.Equal(-1, decision.AvailableBytes);
    }

    [Fact]
    [Trait("Case", "SC-08")]
    public void EvaluateSameVolume_and_EvaluateOutputVolume_clamp_instead_of_overflowing()
    {
        CapacityDecision same = TempCapacity.EvaluateSameVolume(long.MaxValue, long.MaxValue);
        CapacityDecision output = TempCapacity.EvaluateOutputVolume(long.MaxValue, long.MaxValue);

        Assert.Equal(long.MaxValue, same.RequiredBytes);
        Assert.Equal(long.MaxValue, output.RequiredBytes);
        Assert.True(same.RequiredBytes >= 0);
        Assert.True(output.RequiredBytes >= 0);
    }
}
