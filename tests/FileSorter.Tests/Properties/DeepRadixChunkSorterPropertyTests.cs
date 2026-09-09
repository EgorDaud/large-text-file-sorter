using System.Globalization;
using System.Text;
using CsCheck;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using Xunit;

namespace FileSorter.Tests.Properties;

/// <summary>
/// <see cref="ChunkSorter"/>'s radix against a naive sort that calls
/// <see cref="LineOrder.Compare"/> directly, aimed at the deep paths a wide alphabet
/// cannot reach: arbitrary bytes essentially never share a leading run, so the order is
/// decided at the first level. Here the alphabet is three symbols and the string parts
/// run to 60 bytes, which makes long ties the common case, and the lists are long enough
/// that the introsort base case is not the only path taken -- the seam past the cached
/// prefix, bucket 0, the split guard and the depth cap.
/// </summary>
public sealed class DeepRadixChunkSorterPropertyTests
{
    private static readonly Gen<byte> Symbol = Gen.Int[0, 2].Select(i => (byte)i);
    private static readonly Gen<byte[]> TiedStringPart = Symbol.Array[0, 60];

    // A tiny number range for the same reason the alphabet is tiny: the point is to
    // reach the levels below the string comparison, and a wide number range would let
    // Number decide most of the pairs the string part ties on.
    private static readonly Gen<(long Number, byte[] StringPart)> Entry =
        Gen.Select(Gen.Long[0, 4], TiedStringPart);

    private static readonly Gen<List<(long Number, byte[] StringPart)>> Entries = Entry.List[0, 900];

    [Fact]
    [Trait("Case", "PB-14")]
    public void The_radix_produces_the_same_bytes_as_a_naive_LineOrder_sort()
    {
        Entries.Sample(entries => AssertMatchesNaiveSort(entries), iter: 400);
    }

    // Deterministic stress shapes the generated lists above are too small to reach, each
    // chosen for one path: 200,000 lines of up to 60 bytes over three symbols keeps
    // buckets far above the base case for many levels and pushes a sixth of the lines
    // past the depth cap; up to 6 bytes puts almost everything in bucket 0 at the first
    // string-byte level, where the padded prefix cannot decide length; and empty string
    // parts are bucket 0 at every level. The first row builds a 13 MB buffer and sorts
    // it twice; the three together stay inside a few seconds.
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
        // The shape the split guard exists for, and the one that would recurse once per
        // byte without it: 200,000 lines whose string parts are byte-identical and far
        // longer than the depth cap. Every counting pass finds one bucket, so no level
        // recurses at all and the whole chunk is finished by one introsort. The
        // assertion is the same as every other case here; that this returns rather than
        // overflowing the default 1 MiB stack is the other half of what it checks.
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

    // Descriptors are built directly rather than through LineCursor and LineParser: the
    // string parts here are raw byte values, not a line grammar's text, so parsing them
    // back would produce boundaries other than the ones the property intends.
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

    // The observable is the bytes the output would contain, not which descriptor ended
    // up where: the sort is not stable and byte-identical lines are interchangeable.
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
