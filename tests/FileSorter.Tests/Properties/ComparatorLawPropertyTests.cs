using System.Text;
using CsCheck;
using FileSorter.LineFormat;
using FileSorter.Tests.LineFormat;
using Xunit;

namespace FileSorter.Tests.Properties;

public sealed class ComparatorLawPropertyTests
{
    [Fact]
    [Trait("Case", "PB-03")]
    public void Comparing_a_generated_set_is_antisymmetric()
    {
        LineEntryGen.Entries.Sample(entries =>
        {
            (byte[] buffer, List<LineDescriptor> lines) = Build(entries);

            foreach (LineDescriptor x in lines)
            {
                foreach (LineDescriptor y in lines)
                {
                    int forward = LineOrder.Compare(in x, buffer, in y, buffer);
                    int backward = LineOrder.Compare(in y, buffer, in x, buffer);
                    Assert.Equal(Math.Sign(forward), -Math.Sign(backward));
                }
            }
        }, iter: 150);
    }

    [Fact]
    [Trait("Case", "PB-04")]
    public void Comparing_a_generated_set_is_transitive()
    {
        LineEntryGen.Entries.Sample(entries =>
        {
            (byte[] buffer, List<LineDescriptor> lines) = Build(entries);

            foreach (LineDescriptor x in lines)
            {
                foreach (LineDescriptor y in lines)
                {
                    if (LineOrder.Compare(in x, buffer, in y, buffer) > 0)
                    {
                        continue;
                    }

                    foreach (LineDescriptor z in lines)
                    {
                        if (LineOrder.Compare(in y, buffer, in z, buffer) > 0)
                        {
                            continue;
                        }

                        Assert.True(LineOrder.Compare(in x, buffer, in z, buffer) <= 0);
                    }
                }
            }
        }, iter: 100);
    }

    [Fact]
    [Trait("Case", "PB-05")]
    public void Comparing_a_generated_set_is_total()
    {
        LineEntryGen.Entries.Sample(entries =>
        {
            (byte[] buffer, List<LineDescriptor> lines) = Build(entries);

            foreach (LineDescriptor x in lines)
            {
                Assert.Equal(0, LineOrder.Compare(in x, buffer, in x, buffer));

                foreach (LineDescriptor y in lines)
                {
                    int forward = LineOrder.Compare(in x, buffer, in y, buffer);
                    int backward = LineOrder.Compare(in y, buffer, in x, buffer);
                    Assert.True(
                        (forward == 0) == (backward == 0),
                        "Exactly one of before, after, or equal must hold, consistently in both directions.");
                }
            }
        }, iter: 150);
    }

    private static (byte[] Buffer, List<LineDescriptor> Lines) Build(List<(long Number, string StringPart)> entries)
    {
        byte[] buffer = LineEntryGen.BuildInput(entries);
        return (buffer, DescriptorFixture.Build(buffer));
    }

    // Any byte value, at lengths straddling the eight-byte prefix.
    private static readonly Gen<byte[]> ArbitraryStringPart = Gen.Byte.Array[0, 16];

    // stringB shares a head with stringA so prefixes often tie; a 0x00 tail byte can sit where padding would.
    private static readonly Gen<int> SharedHeadLength = Gen.Int[0, 12];
    private static readonly byte[] PaddingAdjacentAlphabet = [0x00, 0x01, 0x7F, 0xFF];
    private static readonly Gen<byte[]> MutationTail = Gen.Int[0, 3].Select(i => PaddingAdjacentAlphabet[i]).Array[0, 8];

    [Fact]
    [Trait("Case", "PB-08")]
    public void Comparing_arbitrary_byte_strings_agrees_in_sign_with_a_reference_compare_that_has_no_prefix_fast_path()
    {
        Gen.Select(ArbitraryStringPart, SharedHeadLength, MutationTail, Gen.Long, Gen.Long).Sample(t =>
        {
            (byte[] stringA, int headLength, byte[] tail, long numberA, long numberB) = t;
            int sharedHead = Math.Min(headLength, stringA.Length);
            byte[] stringB = [.. stringA.AsSpan(0, sharedHead), .. tail];

            (LineDescriptor a, byte[] bufferA) = BuildLine(numberA, stringA);
            (LineDescriptor b, byte[] bufferB) = BuildLine(numberB, stringB);

            int actual = LineOrder.Compare(in a, bufferA, in b, bufferB);
            int reference = ReferenceCompareWithoutPrefix(in a, bufferA, in b, bufferB);

            Assert.Equal(Math.Sign(reference), Math.Sign(actual));
        }, iter: 1000);
    }

    // The number prefix keeps the raw-line span distinct from the string span, or the third level is dead.
    private static (LineDescriptor Descriptor, byte[] Buffer) BuildLine(long number, byte[] stringPart)
    {
        byte[] numberPrefix = Encoding.ASCII.GetBytes($"{number}. ");
        byte[] buffer = [.. numberPrefix, .. stringPart];
        int stringOffset = numberPrefix.Length;
        ulong prefix = LineDescriptor.BuildPrefix(stringPart);
        return (new LineDescriptor(prefix, number, offset: 0, length: buffer.Length, stringOffset), buffer);
    }

    // Deliberately LineOrder.Compare minus the prefix step, so a mismatch isolates the fast path.
    private static int ReferenceCompareWithoutPrefix(
        in LineDescriptor a, ReadOnlySpan<byte> bufferA,
        in LineDescriptor b, ReadOnlySpan<byte> bufferB)
    {
        int stringComparison = bufferA.Slice(a.StringOffset, a.StringLength)
            .SequenceCompareTo(bufferB.Slice(b.StringOffset, b.StringLength));
        if (stringComparison != 0)
        {
            return stringComparison;
        }

        int numberComparison = a.Number.CompareTo(b.Number);
        if (numberComparison != 0)
        {
            return numberComparison;
        }

        return bufferA.Slice(a.Offset, a.Length).SequenceCompareTo(bufferB.Slice(b.Offset, b.Length));
    }
}
