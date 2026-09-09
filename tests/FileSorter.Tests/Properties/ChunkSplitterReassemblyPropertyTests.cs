using CsCheck;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using Xunit;

namespace FileSorter.Tests.Properties;

/// <summary>
/// The chunk splitter's reassembly property. <see cref="ChunkReader"/>, driven the way
/// run generation drives it, is handed a small buffer pool against a generated file, so
/// most inputs are split across several reads and several chunks with carry-over between
/// them. Concatenating every emitted line, in the order the chunks were produced and in
/// the order each chunk lists them, must reproduce the input exactly: no line dropped,
/// duplicated, or reordered by chunking alone.
/// </summary>
public sealed class ChunkSplitterReassemblyPropertyTests
{
    // MaxLineLength is strictly greater than the longest line LineEntryGen can produce
    // (under 48 bytes at the extreme: long.MinValue's 20 characters, ". ", and 16
    // characters of string part), and BufferSize must exceed it, as ChunkReader's
    // constructor requires. The small descriptor capacity forces a chunk boundary every
    // few lines even though the whole generated file is only a few kilobytes.
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

    // "Reproduces the input" means under the terminator rule, not byte for byte:
    // LineEntryGen.BuildInput occasionally writes a string part ending in '\r', and the
    // reader strips the single '\r' immediately before the '\n' written alongside it.
    // NaiveLineFormat.Split is the independent reading of that rule, used here to build
    // the expected bytes rather than to compare a splitter's output against.
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

        // Capacity 2 is enough: nothing here spills concurrently, so the only two slots
        // ever held at once are the chunk being read and the reader's prefetch.
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

                // BuildInput terminates every line, the last one included, with a single
                // '\n', which is what lets this reconstruction match the input.
                reassembled.WriteByte((byte)'\n');
            }

            chunk.Value.Buffer.Dispose();
        }

        return reassembled.ToArray();
    }
}
