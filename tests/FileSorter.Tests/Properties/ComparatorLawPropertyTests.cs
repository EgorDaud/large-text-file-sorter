using System.Text;
using CsCheck;
using FileSorter.LineFormat;
using FileSorter.Tests.LineFormat;
using Xunit;

namespace FileSorter.Tests.Properties;

/// <summary>
/// The ordering laws the unit tests check over one curated set, checked here over a
/// freshly generated set on every iteration: the curated set proves the laws for cases
/// someone thought to write down, this proves them for shapes nobody enumerated. Unlike
/// the byte-identity properties in this folder, these call <see cref="LineOrder"/>
/// directly -- the comparator is the subject here, not something an oracle stands in
/// for.
/// </summary>
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
                // Reflexivity: sharpest failure mode of a broken totality claim.
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

    // The sets above come from LineEntryGen, which is printable ASCII only, so they
    // never put a 0x00 or an arbitrary high byte where the cached prefix would read it.
    // This property covers that gap: raw byte strings of any value, at lengths that
    // straddle the eight-byte prefix boundary from both sides. The reference is
    // LineOrder's own fallback levels, so a disagreement can only mean the prefix fast
    // path picked the wrong sign.
    private static readonly Gen<byte[]> ArbitraryStringPart = Gen.Byte.Array[0, 16];

    // stringB is a mutation of stringA -- a shared head of random length, then an
    // independent tail -- rather than its own independent draw. Two independently drawn
    // byte strings almost never share a leading run, so the case this property exists
    // for (prefixes tie, strings differ) would be reached only a handful of times per
    // run. The tail's alphabet includes 0x00 so that a shared head shorter than eight
    // bytes can still land a real zero byte where padding would otherwise be.
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

    // A real line shape -- a number, ". ", then the string part -- so the raw-line span
    // the third comparison level reads is not the same bytes as the string-part span the
    // first level reads. Point both at one span and the third level can never break a
    // tie the first has not already broken, which makes it dead weight in the property.
    private static (LineDescriptor Descriptor, byte[] Buffer) BuildLine(long number, byte[] stringPart)
    {
        byte[] numberPrefix = Encoding.ASCII.GetBytes($"{number}. ");
        byte[] buffer = [.. numberPrefix, .. stringPart];
        int stringOffset = numberPrefix.Length;
        ulong prefix = LineDescriptor.BuildPrefix(stringPart);
        return (new LineDescriptor(prefix, number, offset: 0, length: buffer.Length, stringOffset), buffer);
    }

    // LineOrder.Compare's three levels minus the prefix step -- what Compare falls
    // through to once prefixes tie. Deliberately identical to that fallback rather than
    // a second independent implementation: the property isolates the prefix fast path,
    // so the reference has to be everything except it.
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
