using System.Text;
using CsCheck;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using Xunit;

namespace FileSorter.Tests.Properties;

public sealed class ChunkSorterPropertyTests
{
    // Arbitrary bytes, unlike LineEntryGen's ASCII, so every top-byte bucket is reachable.
    private static readonly Gen<byte[]> ArbitraryStringPart = Gen.Byte.Array[0, 20];
    private static readonly Gen<(long Number, byte[] StringPart)> Entry = Gen.Select(Gen.Long, ArbitraryStringPart);

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

    // Not via LineParser: arbitrary bytes may contain '\n' or '.', which it would split on.
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
