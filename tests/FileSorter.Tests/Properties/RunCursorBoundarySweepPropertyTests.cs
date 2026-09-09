using CsCheck;
using FileSorter.LineFormat;
using FileSorter.Merging;
using FileSorter.RunGeneration;
using Xunit;

namespace FileSorter.Tests.Properties;

/// <summary>
/// The same boundary sweep as <see cref="ChunkBoundarySweepPropertyTests"/>, driven
/// against <see cref="RunCursor"/> instead of <see cref="ChunkReader"/>: random line
/// lengths (some at exactly the limit), mixed <c>\n</c>/<c>\r\n</c>, random equal-size
/// read-ahead windows, random descriptor capacities, a stream handing back only a few
/// random bytes per read, and the same over-length injection. No BOM, because
/// <see cref="RunCursor"/> never strips one -- a run file this sorter wrote never starts
/// with one -- so <see cref="NaiveLineFormat.Split"/> is run with <c>stripBom: false</c>
/// to match, and with <c>stripCarriageReturn: false</c>, since a run file's '\r' before
/// '\n' is content this sorter carried in. The content is not sorted: the splitting
/// logic under test is oblivious to whether it is.
/// </summary>
public sealed class RunCursorBoundarySweepPropertyTests
{
    private const int MaxLineLength = RandomLineFileGen.MaxLineLength;

    // RunCursor requires a window strictly greater than maxLineLength + 1.
    private static readonly Gen<int> WindowSize = Gen.Int[MaxLineLength + 2, MaxLineLength + 2 + 2048];
    private static readonly Gen<int> DescriptorCapacity = Gen.Int[1, 7];
    private static readonly Gen<int> ReadCap = Gen.Int[1, 64];
    private static readonly Gen<int> MalformedLength = Gen.Int[MaxLineLength + 1, MaxLineLength + 30];

    private sealed record Scenario(
        List<RandomLineFileGen.LineSpec> Lines,
        bool InjectMalformed,
        bool MalformedAtEnd,
        int MalformedIndex,
        int MalformedLength,
        int WindowSize,
        int DescriptorCapacity,
        int ReadCap,
        int StreamSeed);

    // Two independent halves zipped at the end rather than chained through SelectMany:
    // no generator's range depends on an earlier value, since malformedIndex needs only
    // lines.Count and streamSeed, both in hand by the combining step.
    private static readonly Gen<(List<RandomLineFileGen.LineSpec> Lines, int InjectRoll, bool MalformedAtEnd, int MalformedLength)> FileShape =
        Gen.Select(RandomLineFileGen.LineList(RandomLineFileGen.SweepMaxLineCount), Gen.Int[0, 3], Gen.Bool, MalformedLength,
            (lines, injectRoll, malformedAtEnd, malformedLength) => (lines, injectRoll, malformedAtEnd, malformedLength));

    private static readonly Gen<(int WindowSize, int DescriptorCapacity, int ReadCap, int StreamSeed)> ReaderShape =
        Gen.Select(WindowSize, DescriptorCapacity, ReadCap, Gen.Int,
            (windowSize, descriptorCapacity, readCap, streamSeed) => (windowSize, descriptorCapacity, readCap, streamSeed));

    private static readonly Gen<Scenario> ScenarioGen =
        Gen.Select(FileShape, ReaderShape, (file, reader) => new Scenario(
            file.Lines, file.InjectRoll == 0, file.MalformedAtEnd,
            file.Lines.Count == 0 ? 0 : new Random(reader.StreamSeed).Next(file.Lines.Count),
            file.MalformedLength, reader.WindowSize, reader.DescriptorCapacity, reader.ReadCap, reader.StreamSeed));

    [Fact]
    [Trait("Case", "PB-11")]
    public async Task RunCursor_over_random_inputs_matches_the_naive_splitter()
    {
        await ScenarioGen.SampleAsync(async scenario =>
        {
            List<RandomLineFileGen.LineSpec> lines = scenario.InjectMalformed
                ? RandomLineFileGen.InjectMalformedLine(
                    scenario.Lines, scenario.MalformedAtEnd, scenario.MalformedIndex, scenario.MalformedLength)
                : scenario.Lines;
            byte[] input = RandomLineFileGen.BuildFileBytes(hasBom: false, lines);
            NaiveLineFormat.NaiveSplitResult expected =
                NaiveLineFormat.Split(input, MaxLineLength, stripBom: false, stripCarriageReturn: false);

            if (expected.IsMalformed)
            {
                MalformedLineException thrown = await Assert.ThrowsAsync<MalformedLineException>(
                    () => ReadAllAsync(input, scenario));

                Assert.Equal(expected.MalformedOffset, thrown.ByteOffset);
                Assert.Equal(expected.MalformedLineNumber, thrown.LineNumber);
                return;
            }

            List<byte[]> actualLines = await ReadAllAsync(input, scenario);

            Assert.Equal(expected.Lines.Count, actualLines.Count);
            for (int i = 0; i < expected.Lines.Count; i++)
            {
                NaiveLineFormat.NaiveLine line = expected.Lines[i];
                byte[] expectedBytes = input.AsSpan((int)line.Offset, line.Length).ToArray();
                Assert.True(expectedBytes.AsSpan().SequenceEqual(actualLines[i]), $"Line {i} differs.");
            }
        }, iter: 300);
    }

    private static async Task<List<byte[]>> ReadAllAsync(byte[] input, Scenario scenario)
    {
        await using MemoryStream memory = new(input);
        await using LimitedReadStream limited = new(memory, scenario.ReadCap, new Random(scenario.StreamSeed));
        RunCursorBuffers buffers = new(
            new byte[scenario.WindowSize], new byte[scenario.WindowSize], new LineDescriptor[scenario.DescriptorCapacity]);
        RunCursor cursor = new(limited, buffers, MaxLineLength);

        List<byte[]> lines = [];
        while (await cursor.MoveNextAsync(CancellationToken.None))
        {
            LineDescriptor descriptor = cursor.Current;
            lines.Add(cursor.Buffer.AsSpan(descriptor.Offset, descriptor.Length).ToArray());
        }

        await cursor.DisposeAsync();
        return lines;
    }
}
