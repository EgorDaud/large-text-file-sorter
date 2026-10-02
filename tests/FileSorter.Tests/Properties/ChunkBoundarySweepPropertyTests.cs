using CsCheck;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.Properties;

public sealed class ChunkBoundarySweepPropertyTests
{
    private const int MaxLineLength = RandomLineFileGen.MaxLineLength;

    // MaxLineLength + 3 is ChunkReader's smallest accepted buffer.
    private static readonly Gen<int> BufferSize = Gen.Int[MaxLineLength + 3, MaxLineLength + 3 + 2048];
    private static readonly Gen<int> DescriptorCapacity = Gen.Int[1, 7];
    private static readonly Gen<int> PoolCapacity = Gen.Int[2, 4];
    private static readonly Gen<int> ReadCap = Gen.Int[1, 64];
    private static readonly Gen<int> MalformedLength = Gen.Int[MaxLineLength + 1, MaxLineLength + 30];

    private sealed record Scenario(
        bool HasBom,
        List<RandomLineFileGen.LineSpec> Lines,
        bool InjectMalformed,
        bool MalformedAtEnd,
        int MalformedIndex,
        int MalformedLength,
        int BufferSize,
        int DescriptorCapacity,
        int PoolCapacity,
        int ReadCap,
        int StreamSeed);

    private static readonly Gen<(bool HasBom, List<RandomLineFileGen.LineSpec> Lines, int InjectRoll, bool MalformedAtEnd, int MalformedLength)> FileShape =
        Gen.Select(Gen.Bool, RandomLineFileGen.LineList(RandomLineFileGen.SweepMaxLineCount), Gen.Int[0, 3], Gen.Bool, MalformedLength,
            (hasBom, lines, injectRoll, malformedAtEnd, malformedLength) => (hasBom, lines, injectRoll, malformedAtEnd, malformedLength));

    private static readonly Gen<(int BufferSize, int DescriptorCapacity, int PoolCapacity, int ReadCap, int StreamSeed)> ReaderShape =
        Gen.Select(BufferSize, DescriptorCapacity, PoolCapacity, ReadCap, Gen.Int,
            (bufferSize, descriptorCapacity, poolCapacity, readCap, streamSeed) =>
                (bufferSize, descriptorCapacity, poolCapacity, readCap, streamSeed));

    private static readonly Gen<Scenario> ScenarioGen =
        Gen.Select(FileShape, ReaderShape, (file, reader) => new Scenario(
            file.HasBom, file.Lines, file.InjectRoll == 0, file.MalformedAtEnd,
            file.Lines.Count == 0 ? 0 : new Random(reader.StreamSeed).Next(file.Lines.Count),
            file.MalformedLength, reader.BufferSize, reader.DescriptorCapacity, reader.PoolCapacity,
            reader.ReadCap, reader.StreamSeed));

    [Fact]
    [Trait("Case", "PB-10")]
    public async Task ChunkReader_over_random_inputs_matches_the_naive_splitter()
    {
        await ScenarioGen.SampleAsync(async scenario =>
        {
            List<RandomLineFileGen.LineSpec> lines = scenario.InjectMalformed
                ? RandomLineFileGen.InjectMalformedLine(
                    scenario.Lines, scenario.MalformedAtEnd, scenario.MalformedIndex, scenario.MalformedLength)
                : scenario.Lines;
            byte[] input = RandomLineFileGen.BuildFileBytes(scenario.HasBom, lines);
            NaiveLineFormat.NaiveSplitResult expected =
                NaiveLineFormat.Split(input, MaxLineLength, stripBom: true, stripCarriageReturn: true);

            if (expected.IsMalformed)
            {
                MalformedLineException thrown = await Assert.ThrowsAsync<MalformedLineException>(
                    () => ReadAllAsync(input, scenario));

                Assert.Equal(expected.MalformedOffset, thrown.ByteOffset);
                Assert.Equal(expected.MalformedLineNumber, thrown.LineNumber);
                return;
            }

            (List<byte[]> actualLines, long linesRead, long bytesConsumed) = await ReadAllAsync(input, scenario);

            Assert.Equal(expected.Lines.Count, actualLines.Count);
            for (int i = 0; i < expected.Lines.Count; i++)
            {
                NaiveLineFormat.NaiveLine line = expected.Lines[i];
                byte[] expectedBytes = input.AsSpan((int)line.Offset, line.Length).ToArray();
                Assert.True(expectedBytes.AsSpan().SequenceEqual(actualLines[i]), $"Line {i} differs.");
            }

            Assert.Equal(expected.Lines.Count, linesRead);
            Assert.Equal(input.Length, bytesConsumed);
        }, iter: 300);
    }

    private static async Task<(List<byte[]> Lines, long LinesRead, long BytesConsumed)> ReadAllAsync(
        byte[] input, Scenario scenario)
    {
        using MemoryStream memory = new(input);
        await using LimitedReadStream limited = new(memory, scenario.ReadCap, new Random(scenario.StreamSeed));
        BufferPool pool = new(scenario.BufferSize, scenario.DescriptorCapacity, scenario.PoolCapacity);
        await using ChunkReader reader = new(limited, pool, MaxLineLength);

        List<byte[]> lines = [];
        Chunk? chunk;
        while ((chunk = await reader.ReadNextAsync(CancellationToken.None)) is not null)
        {
            for (int i = 0; i < chunk.Value.Count; i++)
            {
                LineDescriptor line = chunk.Value.Buffer.Lines[i];
                lines.Add(chunk.Value.Buffer.Bytes.AsSpan(line.Offset, line.Length).ToArray());
            }

            chunk.Value.Buffer.Dispose();
        }

        return (lines, reader.LinesRead, reader.BytesConsumed);
    }
}
