using System.Globalization;
using System.Text;
using CsCheck;
using FileSorter.Cli;
using FileSorter.Planning;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.Properties;

/// <summary>Runs end to end through <see cref="SortCommand.RunAsync"/>.</summary>
[Collection("Program")]
public sealed class HighByteSortPropertyTests
{
    private const int MaxLineLength = 128;

    private const int MinLines = 12_000;
    private const int MaxLines = 16_000;

    private static readonly (int Parallelism, long BudgetBytes, MemoryPlan Plan)[] Configurations =
        [.. new[] { 2, 4 }.Select(parallelism =>
        {
            long budget = MemoryBudget.MinimumViableBudget(parallelism, MaxLineLength, MemoryBudget.AssumedMeanLineLength);
            return (parallelism, budget,
                MemoryBudget.Calculate(budget, parallelism, MaxLineLength, MemoryBudget.AssumedMeanLineLength));
        })];

    // No token may contain '\r' or '\n': carriage-return semantics would muddy this oracle.
    private static readonly Gen<byte[]> AsciiToken = Gen.Byte[0x20, 0x7E].Select(b => new[] { b });

    private static readonly Gen<byte[]> NonAsciiOrControlToken = Gen.OneOfConst<byte[]>(
        [0x00],
        [0x09],
        [0xC3, 0xA9],
        [0xE2, 0x82, 0xAC],
        [0xF0, 0x9D, 0x84, 0x9E]);

    private static readonly Gen<byte[]> RawHighByteToken = Gen.Byte[0x80, 0xFF].Select(b => new[] { b });

    private static readonly Gen<byte[]> Token = Gen.OneOf(
        AsciiToken, AsciiToken, NonAsciiOrControlToken, NonAsciiOrControlToken, RawHighByteToken, RawHighByteToken);

    private static readonly Gen<byte[]> StringPart =
        Token.List[0, 12].Select(tokens => tokens.SelectMany(t => t).ToArray());

    private static readonly Gen<List<byte[]>> Vocabulary = StringPart.List[24, 64];

    private static readonly Gen<long> Number = Gen.OneOf(Gen.Long[-3, 3], Gen.Long);

    // Rolls 1 and 2 of 18 add one or two leading zeros: the same number, different raw bytes.
    private static readonly Gen<(int StringIndex, long Number, int ZeroPadding)> LineSpec =
        Gen.Select(Gen.Int[0, int.MaxValue], Number, Gen.Int[0, 17], (index, number, roll) =>
            (index, number, roll < 3 ? roll : 0));

    private static readonly Gen<(int ConfigurationIndex, List<byte[]> Vocabulary, List<(int StringIndex, long Number, int ZeroPadding)> Lines)> Scenario =
        Gen.Select(Gen.Int[0, Configurations.Length - 1], Vocabulary, LineSpec.List[MinLines, MaxLines]);

    [Fact]
    [Trait("Case", "PB-24")]
    public async Task Sorting_utf8_and_high_byte_strings_matches_an_independent_ordinal_byte_oracle()
    {
        await Scenario.SampleAsync(async scenario =>
        {
            (int configurationIndex, List<byte[]> vocabulary, List<(int StringIndex, long Number, int ZeroPadding)> specs) = scenario;
            (int parallelism, long budgetBytes, MemoryPlan plan) = Configurations[configurationIndex];

            byte[] input = Render(vocabulary, specs);

            long minimumRuns = Math.Max(
                (input.Length + plan.ChunkSize - 1) / plan.ChunkSize,
                (specs.Count + plan.DescriptorCapacity - 1L) / plan.DescriptorCapacity);
            Assert.True(
                minimumRuns > plan.MergeFanIn,
                $"At least {minimumRuns} runs, but a merge pass takes {plan.MergeFanIn}.");

            byte[] expected = NaiveReferenceSort.Sort(input);
            byte[] actual = await PropertyHarness.RunSorterAsync(
                input, budgetBytes, MaxLineLength, TestContext.Current.CancellationToken, parallelism);

            Assert.Equal(expected, actual);
        }, iter: 100);
    }

    private static byte[] Render(List<byte[]> vocabulary, List<(int StringIndex, long Number, int ZeroPadding)> specs)
    {
        using MemoryStream output = new();
        foreach ((int stringIndex, long number, int zeroPadding) in specs)
        {
            string digits = number.ToString(CultureInfo.InvariantCulture);
            string padded = digits.StartsWith('-')
                ? "-" + new string('0', zeroPadding) + digits[1..]
                : new string('0', zeroPadding) + digits;

            output.Write(Encoding.ASCII.GetBytes(padded + ". "));
            output.Write(vocabulary[stringIndex % vocabulary.Count]);
            output.WriteByte((byte)'\n');
        }

        return output.ToArray();
    }
}
