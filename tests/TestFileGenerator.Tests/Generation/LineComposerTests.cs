using System.Globalization;
using TestFileGenerator.Generation;
using Xunit;

namespace TestFileGenerator.Tests.Generation;

public sealed class LineComposerTests
{
    private const long SmallFileBytes = 16 * 1024;
    private const long MediumFileBytes = 1024 * 1024;
    private const long StatisticalSampleBytes = 2 * 1024 * 1024;

    [Fact]
    [Trait("Case", "GN-01")]
    public void Composing_when_given_a_byte_size_target_fills_it_to_within_one_line()
    {
        const long target = 512 * 1024;

        byte[] output = GeneratedOutput.Write(target);

        // The upper bound catches an overshoot; the lower bound, tight to the longest line
        // the vocabulary can produce, catches a generator that quietly stops far short.
        Assert.InRange((long)output.Length, target - LineComposer.MaxComposedLineLength, target);
    }

    [Fact]
    [Trait("Case", "GN-02")]
    public void Composing_when_the_target_falls_inside_a_line_omits_that_line_entirely()
    {
        const int completedLines = 10;

        byte[] reference = GeneratedOutput.Write(SmallFileBytes);
        IReadOnlyList<GeneratedLine> lines = GeneratedOutput.Parse(reference);
        int completedBytes = lines.Take(completedLines).Sum(line => line.ByteLength + 1);
        int crossingLineBytes = lines[completedLines].ByteLength + 1;

        byte[] output = GeneratedOutput.Write(completedBytes + crossingLineBytes - 1);

        Assert.Equal(reference.AsSpan(0, completedBytes).ToArray(), output);
        Assert.Equal((byte)'\n', output[^1]);
    }

    [Fact]
    [Trait("Case", "GN-02")]
    public void Composing_when_the_line_would_cross_the_remaining_budget_reports_nothing_written()
    {
        LineComposer composer = GeneratedOutput.Composer();
        byte[] destination = new byte[LineComposer.MaxComposedLineLength];

        Assert.False(composer.TryComposeNext(destination, 1, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    [Trait("Case", "GN-03")]
    public void Composing_when_the_duplicate_ratio_is_above_zero_repeats_string_parts()
    {
        IReadOnlyList<GeneratedLine> shared = Lines(SmallFileBytes, duplicateRatio: 0.5);
        IReadOnlyList<GeneratedLine> distinct = Lines(SmallFileBytes, duplicateRatio: 0);

        Assert.True(
            RepeatedShare(shared) > 0.3,
            $"Half the lines were configured to share a string part; {RepeatedShare(shared):P1} of them did.");

        // Zero, not merely few: with no duplicates asked for, none arrive by coincidence
        // either, which is what makes the ratio above a construction rather than a hope.
        Assert.Equal(0d, RepeatedShare(distinct));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [Trait("Case", "GN-04")]
    public void Composing_when_a_duplicate_ratio_is_configured_observes_it_within_tolerance(double ratio)
    {
        const double tolerance = 0.02;

        double measured = RepeatedShare(Lines(StatisticalSampleBytes, duplicateRatio: ratio));

        Assert.InRange(measured, ratio - tolerance, ratio + tolerance);
    }

    [Fact]
    [Trait("Case", "GN-05")]
    public void Composing_when_the_seed_is_unchanged_reproduces_byte_identical_output()
    {
        Assert.Equal(
            GeneratedOutput.Write(SmallFileBytes, seed: 7),
            GeneratedOutput.Write(SmallFileBytes, seed: 7));
    }

    [Fact]
    [Trait("Case", "GN-06")]
    public void Composing_when_the_seed_differs_produces_different_output()
    {
        Assert.NotEqual(
            GeneratedOutput.Write(SmallFileBytes, seed: 7),
            GeneratedOutput.Write(SmallFileBytes, seed: 8));
    }

    [Fact]
    [Trait("Case", "GN-07")]
    public void Composing_when_the_target_is_smaller_than_a_single_line_produces_an_empty_file()
    {
        Assert.Empty(GeneratedOutput.Write(targetBytes: 4));
    }

    [Fact]
    [Trait("Case", "GN-08")]
    public void Composing_when_the_target_is_zero_produces_an_empty_file()
    {
        Assert.Empty(GeneratedOutput.Write(targetBytes: 0));
    }

    [Fact]
    [Trait("Case", "GN-09")]
    public void Composing_when_the_output_is_read_back_produces_only_lines_the_grammar_accepts()
    {
        // Parse throws on anything the settled grammar rejects, so reaching the assertions
        // at all is most of this case.
        IReadOnlyList<GeneratedLine> lines = Lines(MediumFileBytes);

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.InRange(line.ByteLength, 1, LineComposer.MaxComposedLineLength - 1));
    }

    [Fact]
    [Trait("Case", "GN-10")]
    public void Composing_when_the_output_is_read_back_produces_numbers_spanning_a_wide_range()
    {
        long[] numbers = [.. Lines(MediumFileBytes).Select(line => line.Number)];

        Assert.All(numbers, number => Assert.InRange(number, 0, long.MaxValue));
        Assert.True(numbers.Min() < 1_000, $"The smallest number generated was {numbers.Min()}.");
        Assert.True(numbers.Max() > 1_000_000_000_000_000, $"The largest number generated was {numbers.Max()}.");

        int magnitudes = numbers
            .Select(number => number.ToString(CultureInfo.InvariantCulture).Length)
            .Distinct()
            .Count();
        Assert.True(magnitudes >= 15, $"Numbers covered only {magnitudes} distinct digit counts.");
    }

    [Fact]
    [Trait("Case", "GN-11")]
    public void Composing_when_the_output_is_read_back_produces_varied_string_parts()
    {
        string[] parts = [.. Lines(MediumFileBytes).Select(line => line.StringPart)];

        Assert.True(
            parts.Select(part => part.Length).Distinct().Count() >= 10,
            "String parts did not vary in length.");
        Assert.Contains(parts, part => part.Count(character => character == ' ') >= 3);
        Assert.Contains(parts, part => part.Any(character => character > '\u007f'));
    }

    [Fact]
    [Trait("Case", "GN-12")]
    public void Composing_when_the_output_is_inspected_as_bytes_terminates_every_line_with_one_byte()
    {
        byte[] output = GeneratedOutput.Write(SmallFileBytes);

        Assert.DoesNotContain((byte)'\r', output);
        Assert.Equal((byte)'\n', output[^1]);
        Assert.Equal(GeneratedOutput.Parse(output).Count, output.Count(value => value == (byte)'\n'));
    }

    [Fact]
    public void Composing_when_the_destination_cannot_hold_the_longest_line_rejects_the_call()
    {
        LineComposer composer = GeneratedOutput.Composer();
        byte[] destination = new byte[LineComposer.MaxComposedLineLength - 1];

        Assert.Throws<ArgumentException>(() => { composer.TryComposeNext(destination, SmallFileBytes, out _); });
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Constructing_when_the_duplicate_ratio_is_outside_its_range_rejects_the_options(double ratio)
    {
        GeneratorOptions options = new("unused-by-composition", TargetBytes: 0, Seed: 1, ratio);

        Assert.Throws<ArgumentOutOfRangeException>(() => new LineComposer(options));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [Trait("Case", "GN-14")]
    public void Composing_at_the_smallest_size_that_fits_the_ordinary_pair_guarantees_a_repeated_string_part(int seed)
    {
        long pairBytes = OrdinaryPairByteLength(seed);

        IReadOnlyList<GeneratedLine> lines = Lines(pairBytes, duplicateRatio: 0.1, seed);

        Assert.Equal(2, lines.Count);
        Assert.Equal(lines[0].StringPart, lines[1].StringPart);
    }

    public static TheoryData<long, int> SmallSizesAndSeeds()
    {
        TheoryData<long, int> data = [];
        foreach (long size in new long[] { 512, 1024, 733, 2001 })
        {
            foreach (int seed in new[] { 1, 7, 42 })
            {
                data.Add(size, seed);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SmallSizesAndSeeds))]
    [Trait("Case", "GN-15")]
    public void Composing_at_several_small_sizes_and_seeds_guarantees_at_least_one_repeated_string_part(
        long targetBytes, int seed)
    {
        IReadOnlyList<GeneratedLine> lines = Lines(targetBytes, duplicateRatio: 0.1, seed);

        Assert.True(
            RepeatedShare(lines) > 0,
            $"Target {targetBytes} bytes, seed {seed}: no line shared a string part with another.");
    }

    [Fact]
    [Trait("Case", "GN-16")]
    public void Composing_when_the_target_is_one_byte_short_of_the_minimal_pair_falls_back_without_error()
    {
        const int seed = 42;
        long tooSmall = MinimalPairByteLength(seed) - 1;

        // One byte short of the guarantee's true floor -- the minimal fallback pair, not
        // merely the ordinary one GN-14 pins -- no forced repeat is owed here, only the
        // pre-existing rules: the ceiling is never exceeded, every line is complete, and
        // the same seed reproduces the same bytes. This is the "file is simply too small"
        // case named in the guarantee above, pinned so a regression that starts throwing
        // or overshooting at this exact boundary is caught.
        byte[] first = GeneratedOutput.Write(tooSmall, duplicateRatio: 0.1, seed);
        byte[] second = GeneratedOutput.Write(tooSmall, duplicateRatio: 0.1, seed);

        Assert.Equal(first, second);
        Assert.True(first.LongLength <= tooSmall);
        GeneratedOutput.Parse(first); // throws on anything the line grammar rejects
    }

    [Fact]
    [Trait("Case", "GN-17")]
    public void Composing_at_a_size_that_fits_two_ordinary_lines_but_not_the_drawn_pair_still_guarantees_a_repeat()
    {
        // At 100 bytes, seed 42, the drawn pool entry and numbers are long enough that the
        // ordinary pair does not fit, while two ordinary lines do. Only the minimal pair
        // can carry the guarantee here: a composer that tried the drawn pair alone would
        // produce a two-line file with no repeated string part.
        const long targetBytes = 100;
        const int seed = 42;

        IReadOnlyList<GeneratedLine> lines = Lines(targetBytes, duplicateRatio: 0.1, seed);

        Assert.True(lines.Count >= 2, $"Expected at least two lines at {targetBytes} bytes; got {lines.Count}.");
        Assert.True(
            RepeatedShare(lines) > 0,
            $"Target {targetBytes} bytes, seed {seed}: no line shared a string part with another.");
    }

    [Fact]
    [Trait("Case", "GN-18")]
    public void Composing_over_a_sweep_of_small_sizes_and_seeds_every_multi_line_file_has_a_repeated_string_part()
    {
        // Sizes 40 through 200 in steps of 10, seeds 1 through 20: the band where the
        // drawn pair can outgrow a target that two ordinary lines fit, GN-17's shape at
        // every size that can show it. Every file with two or more lines must have a
        // repeat; a file of zero or one line has nothing to converge over and is outside
        // what the guarantee promises. One test rather than 340, so a failure names every
        // offending (size, seed) at once and the suite's count stays a count of behaviours.
        List<string> failures = [];
        for (long targetBytes = 40; targetBytes <= 200; targetBytes += 10)
        {
            for (int seed = 1; seed <= 20; seed++)
            {
                IReadOnlyList<GeneratedLine> lines = Lines(targetBytes, duplicateRatio: 0.1, seed);
                if (lines.Count >= 2 && RepeatedShare(lines) == 0)
                {
                    failures.Add($"{targetBytes} bytes, seed {seed}: {lines.Count} lines");
                }
            }
        }

        Assert.True(failures.Count == 0, "No repeated string part at: " + string.Join("; ", failures));
    }

    /// <summary>The combined byte length of the ordinary forced pair this seed's composer
    /// draws first, measured directly against a generously large budget rather than
    /// assumed.</summary>
    private static long OrdinaryPairByteLength(int seed)
    {
        LineComposer composer = GeneratedOutput.Composer(seed, duplicateRatio: 0.1);
        byte[] first = new byte[LineComposer.MaxComposedLineLength];
        byte[] second = new byte[LineComposer.MaxComposedLineLength];

        Assert.True(composer.TryComposeNext(first, long.MaxValue, out int firstLength));
        Assert.True(composer.TryComposeNext(second, long.MaxValue, out int secondLength));

        return firstLength + secondLength;
    }

    /// <summary>The guarantee's true floor for this seed: the minimal fallback pair's
    /// combined byte length, read directly off the composer rather than assumed.</summary>
    private static long MinimalPairByteLength(int seed) =>
        GeneratedOutput.Composer(seed, duplicateRatio: 0.1).MinimalForcedPairByteLength;

    private static IReadOnlyList<GeneratedLine> Lines(long targetBytes, double duplicateRatio = 0.1) =>
        GeneratedOutput.Parse(GeneratedOutput.Write(targetBytes, duplicateRatio));

    private static IReadOnlyList<GeneratedLine> Lines(long targetBytes, double duplicateRatio, int seed) =>
        GeneratedOutput.Parse(GeneratedOutput.Write(targetBytes, duplicateRatio, seed));

    /// <summary>The proportion of lines whose string part appears more than once.</summary>
    private static double RepeatedShare(IReadOnlyList<GeneratedLine> lines)
    {
        Dictionary<string, int> occurrences = [];
        foreach (GeneratedLine line in lines)
        {
            occurrences[line.StringPart] = occurrences.GetValueOrDefault(line.StringPart) + 1;
        }

        return (double)lines.Count(line => occurrences[line.StringPart] > 1) / lines.Count;
    }
}
