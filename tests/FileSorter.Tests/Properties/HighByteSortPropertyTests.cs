using System.Globalization;
using System.Text;
using CsCheck;
using FileSorter.Startup;
using Xunit;

namespace FileSorter.Tests.Properties;

/// <summary>
/// Sorts input whose string parts are drawn from a UTF-8 and high-byte alphabet through
/// <see cref="Program.RunAsync"/> and compares the output byte for byte with an oracle
/// written in this file. The oracle parses each line and orders the lines with
/// <see cref="MemoryExtensions.SequenceCompareTo{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/>
/// alone, so it shares no code with the sorter: a signed comparison of bytes at or above
/// 0x80, or a prefix shortcut that mishandles <c>\0</c>, would produce the same wrong
/// order on both sides if the oracle were built from <c>LineOrder</c>. The
/// printable-ASCII generator behind PB-01 and PB-02 can never reach those bytes.
/// </summary>
[Collection("Program")]
public sealed class HighByteSortPropertyTests
{
    // Longest line the generators can produce: a 20-character number, two extra leading
    // zeros, ". " and at most 12 tokens of up to 4 bytes each, which is well under 128.
    private const int MaxLineLength = 128;

    // The same value Program.AssumedMeanFor clamps to; Program's own constant is private.
    private const int AssumedMeanLineLength = 32;

    // Many more lines than one chunk holds at either parallelism, so run generation
    // spills several runs and the merge needs more than one pass.
    private const int MinLines = 12_000;
    private const int MaxLines = 16_000;

    private static readonly (int Parallelism, long BudgetBytes, MemoryPlan Plan)[] Configurations =
        [.. new[] { 2, 4 }.Select(parallelism =>
        {
            // The minimum viable budget: the smallest chunk the arithmetic allows and the
            // minimum merge fan-in of 2.
            long budget = MemoryBudget.MinimumViableBudget(parallelism, MaxLineLength, AssumedMeanLineLength);
            return (parallelism, budget,
                MemoryBudget.Calculate(budget, parallelism, MaxLineLength, AssumedMeanLineLength));
        })];

    // Never '\n', and no '\r' at all: carriage-return semantics are covered by PB-01 and
    // the end-of-file cases, and would only muddy this oracle.
    private static readonly Gen<byte[]> AsciiToken = Gen.Byte[0x20, 0x7E].Select(b => new[] { b });

    private static readonly Gen<byte[]> NonAsciiOrControlToken = Gen.OneOfConst<byte[]>(
        [0x00],                          // NUL
        [0x09],                          // tab
        [0xC3, 0xA9],                    // "é", 2 bytes
        [0xE2, 0x82, 0xAC],              // "€", 3 bytes
        [0xF0, 0x9D, 0x84, 0x9E]);       // "𝄞", 4 bytes

    // Any byte from 0x80 to 0xFF on its own: continuation bytes without a lead, a lead
    // without its continuations, and 0xF8 to 0xFF, which are never valid UTF-8.
    private static readonly Gen<byte[]> RawHighByteToken = Gen.Byte[0x80, 0xFF].Select(b => new[] { b });

    private static readonly Gen<byte[]> Token = Gen.OneOf(
        AsciiToken, AsciiToken, NonAsciiOrControlToken, NonAsciiOrControlToken, RawHighByteToken, RawHighByteToken);

    // A small vocabulary, drawn from by index below, so many lines share a string part
    // and the number and raw-byte levels of the order are reached constantly.
    private static readonly Gen<byte[]> StringPart =
        Token.List[0, 12].Select(tokens => tokens.SelectMany(t => t).ToArray());

    private static readonly Gen<List<byte[]>> Vocabulary = StringPart.List[24, 64];

    // Half the numbers come from a handful of values, so identical lines occur; the rest
    // span the whole Int64 range, negatives and both extremes included.
    private static readonly Gen<long> Number = Gen.OneOf(Gen.Long[-3, 3], Gen.Long);

    // One draw in six pads the digits with up to two leading zeros after any sign: the
    // same number, different raw bytes.
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

            // A chunk holds at most ChunkSize bytes and DescriptorCapacity lines, so the
            // input needs at least this many runs. More than the merge fan-in means the
            // merge takes more than one pass; the property cannot pass on one run or one pass.
            long minimumRuns = Math.Max(
                (input.Length + plan.ChunkSize - 1) / plan.ChunkSize,
                (specs.Count + plan.DescriptorCapacity - 1L) / plan.DescriptorCapacity);
            Assert.True(
                minimumRuns > plan.MergeFanIn,
                $"At least {minimumRuns} runs, but a merge pass takes {plan.MergeFanIn}.");

            byte[] expected = OracleSort(input);
            byte[] actual = await PropertyHarness.RunSorterAsync(
                input, budgetBytes, MaxLineLength, TestContext.Current.CancellationToken, parallelism);

            Assert.Equal(expected, actual);
        }, iter: 100);
    }

    // "<number>. <string part>\n", with every string part and number as drawn.
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

    // Splits at '\n' (every line here is terminated and none contains '\r'), parses each
    // line at its first '.', and orders by string bytes, then number, then raw line bytes.
    private static byte[] OracleSort(byte[] input)
    {
        List<(byte[] Line, byte[] StringPart, long Number)> lines = [];
        int start = 0;
        while (start < input.Length)
        {
            int end = Array.IndexOf(input, (byte)'\n', start);
            byte[] line = input[start..end];
            start = end + 1;

            int dot = Array.IndexOf(line, (byte)'.');
            long number = long.Parse(
                Encoding.ASCII.GetString(line, 0, dot), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            int stringStart = dot + 1 < line.Length && line[dot + 1] == (byte)' ' ? dot + 2 : dot + 1;
            lines.Add((line, line[stringStart..], number));
        }

        lines.Sort((a, b) =>
        {
            int byString = a.StringPart.AsSpan().SequenceCompareTo(b.StringPart);
            if (byString != 0)
            {
                return byString;
            }

            int byNumber = a.Number.CompareTo(b.Number);
            return byNumber != 0 ? byNumber : a.Line.AsSpan().SequenceCompareTo(b.Line);
        });

        using MemoryStream output = new();
        foreach ((byte[] line, _, _) in lines)
        {
            output.Write(line);
            output.WriteByte((byte)'\n');
        }

        return output.ToArray();
    }
}
