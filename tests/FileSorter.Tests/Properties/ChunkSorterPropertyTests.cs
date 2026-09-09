using System.Text;
using CsCheck;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using Xunit;

namespace FileSorter.Tests.Properties;

/// <summary>
/// <see cref="ChunkSorter"/>'s bucket-then-introsort against a naive sort that calls
/// <see cref="LineOrder.Compare"/> directly with no bucketing pass: splitting a chunk
/// into buckets before sorting each one must change nothing about the sequence of bytes
/// the sort produces. Unlike <c>LineEntryGen</c>, which is printable ASCII only, the
/// string parts here are arbitrary bytes, so all 256 top bytes -- and so every bucket --
/// are reachable.
/// </summary>
public sealed class ChunkSorterPropertyTests
{
    private static readonly Gen<byte[]> ArbitraryStringPart = Gen.Byte.Array[0, 20];
    private static readonly Gen<(long Number, byte[] StringPart)> Entry = Gen.Select(Gen.Long, ArbitraryStringPart);

    // Up to 400 entries so that, spread over 256 buckets, both "many buckets empty"
    // and "one bucket holds a real introsort's worth of lines" are exercised across
    // the iteration budget, not only the near-empty-chunk shapes a small count
    // would leave as the common case.
    private static readonly Gen<List<(long Number, byte[] StringPart)>> Entries = Entry.List[0, 400];

    [Fact]
    [Trait("Case", "PB-09")]
    public void Bucketing_before_the_introsort_produces_the_same_order_as_sorting_without_it()
    {
        Entries.Sample(entries =>
        {
            (byte[] buffer, LineDescriptor[] descriptors) = Build(entries);

            LineDescriptor[] naive = (LineDescriptor[])descriptors.Clone();
            Array.Sort(naive, (a, b) => LineOrder.Compare(in a, buffer, in b, buffer));

            LineDescriptor[] bucketed = (LineDescriptor[])descriptors.Clone();
            ChunkSorter.Sort(bucketed, buffer);

            Assert.Equal(ConcatenatedLines(naive, buffer), ConcatenatedLines(bucketed, buffer));
        }, iter: 1000);
    }

    // Descriptors are built directly rather than through LineCursor/LineParser: the
    // string parts here are arbitrary bytes and may contain '\n' or '.', which the real
    // grammar would read back as different boundaries than this test intends. The sort
    // is the subject, not the splitter, so the offsets are handed over already known.
    private static (byte[] Buffer, LineDescriptor[] Descriptors) Build(
        List<(long Number, byte[] StringPart)> entries)
    {
        using MemoryStream bufferStream = new();
        LineDescriptor[] descriptors = new LineDescriptor[entries.Count];

        for (int i = 0; i < entries.Count; i++)
        {
            (long number, byte[] stringPart) = entries[i];
            byte[] numberPrefix = Encoding.ASCII.GetBytes($"{number}. ");

            int offset = (int)bufferStream.Length;
            bufferStream.Write(numberPrefix);
            int stringOffset = (int)bufferStream.Length;
            bufferStream.Write(stringPart);
            int length = (int)bufferStream.Length - offset;

            ulong prefix = LineDescriptor.BuildPrefix(stringPart);
            descriptors[i] = new LineDescriptor(prefix, number, offset, length, stringOffset);
        }

        return (bufferStream.ToArray(), descriptors);
    }

    // Comparing concatenated raw bytes rather than the descriptor arrays themselves:
    // the sort is not stable, so the two sorts need not agree on which of two
    // byte-identical lines came from which entry, only on the bytes of the output.
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
