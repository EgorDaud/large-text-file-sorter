using CsCheck;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.Properties;

public sealed class ChunkSplitterReassemblyPropertyTests
{
    private const int MaxLineLength = 56;
    private const int BufferSize = 96;
    private const int DescriptorCapacity = 3;

    [Fact]
    [Trait("Case", "PB-06")]
    public async Task Concatenating_every_chunks_lines_in_order_reproduces_the_input()
    {
        await LineEntryGen.Entries.SampleAsync(async entries =>
        {
            byte[] input = LineEntryGen.BuildInput(entries);
            byte[] expected = NormalizedExpected(input);
            byte[] reassembled = await ReadAllChunksAsync(input);

            Assert.Equal(expected, reassembled);
        }, iter: 150);
    }

    // Not the raw input: BuildInput can end a string part in '\r', which the reader strips before '\n'.
    private static byte[] NormalizedExpected(byte[] input)
    {
        NaiveLineFormat.NaiveSplitResult split =
            NaiveLineFormat.Split(input, MaxLineLength, stripBom: false, stripCarriageReturn: true);
        using MemoryStream normalized = new();
        foreach (NaiveLineFormat.NaiveLine line in split.Lines)
        {
            normalized.Write(input, (int)line.Offset, line.Length);
            normalized.WriteByte((byte)'\n');
        }

        return normalized.ToArray();
    }

    private static async Task<byte[]> ReadAllChunksAsync(byte[] input)
    {
        using MemoryStream stream = new(input);

        // Two slots: the chunk being read and the reader's prefetch.
        BufferPool pool = new(BufferSize, DescriptorCapacity, capacity: 2);
        await using ChunkReader reader = new(stream, pool, MaxLineLength);

        using MemoryStream reassembled = new();
        Chunk? chunk;
        while ((chunk = await reader.ReadNextAsync(CancellationToken.None)) is not null)
        {
            for (int i = 0; i < chunk.Value.Count; i++)
            {
                LineDescriptor line = chunk.Value.Buffer.Lines[i];
                reassembled.Write(chunk.Value.Buffer.Bytes, line.Offset, line.Length);
                reassembled.WriteByte((byte)'\n');
            }

            chunk.Value.Buffer.Dispose();
        }

        return reassembled.ToArray();
    }
}
