using System.Globalization;
using System.Text;
using CsCheck;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using Xunit;

namespace FileSorter.Tests.Properties;

public sealed class DeepRadixChunkSorterPropertyTests
{
    // A three-symbol alphabet makes long shared runs common, so the radix reaches its deep levels.
    private static readonly Gen<byte> Symbol = Gen.Int[0, 2].Select(i => (byte)i);
    private static readonly Gen<byte[]> TiedStringPart = Symbol.Array[0, 60];

    private static readonly Gen<(long Number, byte[] StringPart)> Entry =
        Gen.Select(Gen.Long[0, 4], TiedStringPart);

    private static readonly Gen<List<(long Number, byte[] StringPart)>> Entries = Entry.List[0, 900];

    [Fact]
    [Trait("Case", "PB-14")]
    public void The_radix_produces_the_same_bytes_as_a_naive_LineOrder_sort()
    {
        Entries.Sample(entries => AssertMatchesNaiveSort(entries), iter: 400);
    }

    // Rows target the depth cap, bucket 0 at the first string level, and bucket 0 at every level.
    [Theory]
    [Trait("Case", "PB-14")]
    [InlineData(200_000, 60)]
    [InlineData(200_000, 6)]
    [InlineData(50_000, 0)]
    public void The_radix_matches_a_naive_sort_on_a_large_deliberately_tied_chunk(int count, int maxLength)
    {
        Random random = new(20260908);
        List<(long Number, byte[] StringPart)> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            byte[] part = new byte[maxLength == 0 ? 0 : random.Next(0, maxLength + 1)];
            for (int j = 0; j < part.Length; j++)
            {
                part[j] = (byte)random.Next(0, 3);
            }

            entries.Add((random.Next(0, 5), part));
        }

        AssertMatchesNaiveSort(entries);
    }

    [Fact]
    [Trait("Case", "PB-14")]
    public void The_radix_does_not_grow_the_stack_on_a_chunk_that_never_splits()
    {
        // Without the split guard, identical string parts would recurse once per byte.
        const int Count = 200_000;
        byte[] identical = new byte[60];
        Array.Fill(identical, (byte)'x');

        List<(long Number, byte[] StringPart)> entries = new(Count);
        for (int i = 0; i < Count; i++)
        {
            entries.Add((Count - i, identical));
        }

        AssertMatchesNaiveSort(entries);
    }

    private static void AssertMatchesNaiveSort(List<(long Number, byte[] StringPart)> entries)
    {
        (byte[] buffer, LineDescriptor[] descriptors) = Build(entries);

        LineDescriptor[] naive = (LineDescriptor[])descriptors.Clone();
        Array.Sort(naive, (a, b) => LineOrder.Compare(in a, buffer, in b, buffer));

        LineDescriptor[] sorted = (LineDescriptor[])descriptors.Clone();
        ChunkSorter.Sort(sorted, buffer);

        Assert.Equal(ConcatenatedLines(naive, buffer), ConcatenatedLines(sorted, buffer));
    }

    private static (byte[] Buffer, LineDescriptor[] Descriptors) Build(
        List<(long Number, byte[] StringPart)> entries)
    {
        using MemoryStream bufferStream = new();
        LineDescriptor[] descriptors = new LineDescriptor[entries.Count];
        List<(int Offset, int Length, int StringOffset)> layout = new(entries.Count);

        foreach ((long number, byte[] stringPart) in entries)
        {
            int offset = (int)bufferStream.Length;
            bufferStream.Write(Encoding.ASCII.GetBytes(number.ToString(CultureInfo.InvariantCulture) + ". "));
            int stringOffset = (int)bufferStream.Length;
            bufferStream.Write(stringPart);
            layout.Add((offset, (int)bufferStream.Length - offset, stringOffset));
        }

        byte[] buffer = bufferStream.ToArray();
        for (int i = 0; i < layout.Count; i++)
        {
            (int offset, int length, int stringOffset) = layout[i];
            ulong prefix = LineDescriptor.BuildPrefix(
                buffer.AsSpan(stringOffset, LineDescriptor.StringLengthOf(offset, length, stringOffset)));
            descriptors[i] = new LineDescriptor(prefix, entries[i].Number, offset, length, stringOffset);
        }

        return (buffer, descriptors);
    }

    // Compares bytes, not descriptor order: the sort is not stable.
    private static byte[] ConcatenatedLines(LineDescriptor[] descriptors, byte[] buffer)
    {
        using MemoryStream output = new();
        foreach (LineDescriptor descriptor in descriptors)
        {
            output.Write(buffer, descriptor.Offset, descriptor.Length);
            output.WriteByte((byte)'\n');
        }

        return output.ToArray();
    }
}
