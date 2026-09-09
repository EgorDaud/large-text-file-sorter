using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using Xunit;

namespace FileSorter.Tests.LineFormat;

public sealed class LineDescriptorTests
{
    [Fact]
    public void Sizing_the_descriptor_at_runtime_is_thirty_two_bytes_with_no_reference_field()
    {
        Assert.Equal(32, Unsafe.SizeOf<LineDescriptor>());
        Assert.All(
            typeof(LineDescriptor).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            field => Assert.True(field.FieldType.IsValueType, $"{field.Name} is a reference field."));
    }

    [Fact]
    [Trait("Case", "CM-01")]
    public void Building_descriptors_over_several_lines_records_exact_offset_and_length_for_every_line()
    {
        byte[] buffer = "3. Apple\n12. Banana is yellow\n0. \n"u8.ToArray();

        List<LineDescriptor> descriptors = DescriptorFixture.Build(buffer);

        Assert.Equal(3, descriptors.Count);
        AssertReproduces(buffer, descriptors[0], "3. Apple");
        AssertReproduces(buffer, descriptors[1], "12. Banana is yellow");
        AssertReproduces(buffer, descriptors[2], "0. ");
    }

    [Fact]
    [Trait("Case", "CM-03")]
    public void Building_descriptors_handles_the_first_line_of_a_chunk_when_it_sorts_first()
    {
        byte[] buffer = "1. Apple\n2. Zebra\n"u8.ToArray();
        List<LineDescriptor> descriptors = DescriptorFixture.Build(buffer);
        LineDescriptor firstInSource = descriptors[0];
        AssertReproduces(buffer, firstInSource, "1. Apple");

        descriptors.Sort((x, y) => LineOrder.Compare(in x, buffer, in y, buffer));

        Assert.Equal(firstInSource.Offset, descriptors[0].Offset);
    }

    [Fact]
    [Trait("Case", "CM-03")]
    public void Building_descriptors_handles_the_first_line_of_a_chunk_when_it_sorts_last()
    {
        byte[] buffer = "1. Zebra\n2. Apple\n"u8.ToArray();
        List<LineDescriptor> descriptors = DescriptorFixture.Build(buffer);
        LineDescriptor firstInSource = descriptors[0];
        AssertReproduces(buffer, firstInSource, "1. Zebra");

        descriptors.Sort((x, y) => LineOrder.Compare(in x, buffer, in y, buffer));

        Assert.Equal(firstInSource.Offset, descriptors[^1].Offset);
    }

    [Fact]
    [Trait("Case", "CM-04")]
    public void Building_descriptors_handles_the_last_line_with_and_without_a_trailing_terminator()
    {
        byte[] withoutTerminator = "1. Apple\n2. Banana"u8.ToArray();
        byte[] withTerminator = "1. Apple\n2. Banana\n"u8.ToArray();

        List<LineDescriptor> a = DescriptorFixture.Build(withoutTerminator);
        List<LineDescriptor> b = DescriptorFixture.Build(withTerminator);

        Assert.Single(a); // the unterminated tail is carry-over, not a complete line
        AssertReproduces(withTerminator, b[1], "2. Banana");
    }

    [Fact]
    [Trait("Case", "CM-06")]
    public void Building_descriptors_over_an_empty_chunk_produces_no_descriptors()
    {
        List<LineDescriptor> descriptors = DescriptorFixture.Build([]);

        Assert.Empty(descriptors);
        descriptors.Sort((x, y) => LineOrder.Compare(in x, [], in y, [])); // no-op, must not throw
    }

    [Fact]
    [Trait("Case", "CM-07")]
    public void Building_descriptors_carries_the_parsed_number_alongside_the_extent()
    {
        byte[] buffer = Encoding.UTF8.GetBytes(
            $"{long.MinValue}. Apple\n-5. Banana\n0. Cherry\n{long.MaxValue}. Date\n");

        List<LineDescriptor> descriptors = DescriptorFixture.Build(buffer);

        Assert.Equal([long.MinValue, -5, 0, long.MaxValue], [.. descriptors.Select(d => d.Number)]);
    }

    [Fact]
    [Trait("Case", "CM-02")]
    public async Task Sorting_a_reverse_ordered_chunk_leaves_the_underlying_bytes_untouched()
    {
        byte[] buffer = "3. Cherry\n2. Banana\n1. Apple\n"u8.ToArray();
        byte[] original = (byte[])buffer.Clone();
        Chunk chunk = await BuildChunkAsync(buffer);

        ChunkSorter.Sort(chunk.Buffer.Lines.AsSpan(0, chunk.Count), chunk.Buffer.Bytes);

        Assert.Equal(original, chunk.Buffer.Bytes); // only the descriptor array moved
        chunk.Buffer.Dispose();
    }

    [Fact]
    [Trait("Case", "CM-05")]
    public async Task Sorting_a_chunk_of_exactly_one_line_is_a_no_op()
    {
        byte[] buffer = "9. Only\n"u8.ToArray();
        Chunk chunk = await BuildChunkAsync(buffer);

        Assert.Equal(1, chunk.Count);
        ChunkSorter.Sort(chunk.Buffer.Lines.AsSpan(0, chunk.Count), chunk.Buffer.Bytes);

        AssertReproduces(chunk.Buffer.Bytes, chunk.Buffer.Lines[0], "9. Only");
        chunk.Buffer.Dispose();
    }

    [Fact]
    [Trait("Case", "CM-08")]
    public async Task Writing_a_sorted_chunk_back_out_reproduces_a_permutation_of_the_input_lines()
    {
        string[] original = ["3. Cherry", "1. Apple", "2. Banana"];
        byte[] buffer = Encoding.UTF8.GetBytes(string.Concat(original.Select(line => line + "\n")));
        Chunk chunk = await BuildChunkAsync(buffer);

        ChunkSorter.Sort(chunk.Buffer.Lines.AsSpan(0, chunk.Count), chunk.Buffer.Bytes);

        using MemoryStream output = new();
        await WriteChunkAsync(chunk, output);
        string[] written = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(original.OrderBy(x => x, StringComparer.Ordinal), written.OrderBy(x => x, StringComparer.Ordinal));
        chunk.Buffer.Dispose();
    }

    [Fact]
    [Trait("Case", "CM-09")]
    public async Task Writing_a_chunk_built_from_the_two_character_convention_emits_single_character_terminators_only()
    {
        byte[] buffer = "2. Banana\r\n1. Apple\r\n"u8.ToArray();
        Chunk chunk = await BuildChunkAsync(buffer);

        ChunkSorter.Sort(chunk.Buffer.Lines.AsSpan(0, chunk.Count), chunk.Buffer.Bytes);

        using MemoryStream output = new();
        await WriteChunkAsync(chunk, output);
        byte[] written = output.ToArray();

        Assert.DoesNotContain((byte)'\r', written);
        Assert.Equal((byte)'\n', written[^1]); // the last line is terminated too
        Assert.Equal(
            ["1. Apple", "2. Banana"],
            Encoding.UTF8.GetString(written).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        chunk.Buffer.Dispose();
    }

    private static async Task<Chunk> BuildChunkAsync(byte[] buffer)
    {
        BufferPool pool = new(bufferSize: Math.Max(buffer.Length, 1), descriptorCapacity: buffer.Length + 1, capacity: 1);
        PooledBuffer slot = await pool.AcquireAsync(TestContext.Current.CancellationToken);
        buffer.CopyTo(slot.Bytes, 0);

        int count = 0;
        LineCursor cursor = new(slot.Bytes.AsSpan(0, buffer.Length), 64 * 1024);
        while (cursor.TryReadLine(out int offset, out int length))
        {
            ReadOnlySpan<byte> line = slot.Bytes.AsSpan(offset, length);
            if (!LineParser.TryParse(line, out long number, out int stringStart))
            {
                throw new FormatException($"Line at offset {offset} does not match the settled grammar.");
            }

            int stringOffset = offset + stringStart;
            int stringLength = LineDescriptor.StringLengthOf(offset, length, stringOffset);
            ulong prefix = LineDescriptor.BuildPrefix(slot.Bytes.AsSpan(stringOffset, stringLength));
            slot.Lines[count++] = new LineDescriptor(prefix, number, offset, length, stringOffset);
        }

        return new Chunk(slot, count);
    }

    // Mirrors ChunkSpiller's write loop against a generic Stream rather than the real
    // file it always opens, so the chunk-model reconstruction property is testable
    // without touching disk.
    private static async Task WriteChunkAsync(Chunk chunk, Stream output)
    {
        for (int i = 0; i < chunk.Count; i++)
        {
            LineDescriptor line = chunk.Buffer.Lines[i];
            await output.WriteAsync(chunk.Buffer.Bytes.AsMemory(line.Offset, line.Length));
            await output.WriteAsync(new byte[] { (byte)'\n' });
        }
    }

    private static void AssertReproduces(byte[] buffer, LineDescriptor descriptor, string expected) =>
        Assert.Equal(expected, Encoding.UTF8.GetString(buffer, descriptor.Offset, descriptor.Length));
}
