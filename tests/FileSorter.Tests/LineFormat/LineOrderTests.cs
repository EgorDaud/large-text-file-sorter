using System.Text;
using FileSorter.LineFormat;
using Xunit;

namespace FileSorter.Tests.LineFormat;

public sealed class LineOrderTests
{
    [Fact]
    [Trait("Case", "OC-01")]
    public void Comparing_two_lines_orders_by_string_part_ascending_over_the_number()
    {
        Assert.True(CompareLines("9. Apple", "1. Banana is yellow") < 0);
    }

    [Fact]
    [Trait("Case", "OC-02")]
    public void Comparing_two_lines_with_identical_string_parts_breaks_the_tie_by_number()
    {
        Assert.True(CompareLines("30. Apple", "4. Apple") > 0);
    }

    [Fact]
    [Trait("Case", "OC-03")]
    public void Comparing_two_byte_identical_lines_reports_equality_in_both_directions()
    {
        Assert.Equal(0, CompareLines("42. Same", "42. Same"));
        Assert.Equal(0, CompareLines("42. Same", "42. Same"));
    }

    [Fact]
    [Trait("Case", "OC-04")]
    public void Comparing_a_strict_prefix_against_its_extension_orders_the_prefix_first()
    {
        Assert.True(CompareLines("1. Ban", "1. Banana") < 0);
    }

    [Fact]
    [Trait("Case", "OC-05")]
    public void Comparing_an_empty_string_part_against_a_non_empty_one_orders_the_empty_part_first()
    {
        Assert.True(CompareLines("5. ", "5. A") < 0);
    }

    [Fact]
    [Trait("Case", "OC-06")]
    public void Comparing_case_variants_orders_uppercase_before_lowercase()
    {
        Assert.True(CompareLines("1. apple", "1. Apple") > 0);
    }

    [Fact]
    [Trait("Case", "OC-07")]
    public void Comparing_multi_byte_content_orders_by_code_point()
    {
        // The last two-byte code point against the first three-byte one: if the
        // comparator ever stopped being purely byte-wise, this is where it would show.
        string lower = new((char)0x07FF, 1);
        string higher = new((char)0x0800, 1);

        Assert.True(CompareLines($"1. {lower}", $"1. {higher}") < 0);
    }

    [Fact]
    [Trait("Case", "OC-08")]
    public void Comparing_ascii_against_multi_byte_content_orders_ascii_first()
    {
        Assert.True(CompareLines("1. zzz", "1. é") < 0);
    }

    [Fact]
    [Trait("Case", "OC-12")]
    public void Comparing_the_same_logical_lines_at_different_buffer_offsets_agrees()
    {
        byte[] bufferA = [.. "9. filler\n"u8.ToArray(), .. "1. Apple\n"u8.ToArray()];
        byte[] bufferB = "1. Apple\n"u8.ToArray();
        LineDescriptor a = DescriptorFixture.Build(bufferA)[1];
        LineDescriptor b = DescriptorFixture.Build(bufferB)[0];

        LineDescriptor other = DescriptorFixture.Build("1. Banana\n"u8.ToArray())[0];
        byte[] otherBuffer = "1. Banana\n"u8.ToArray();

        Assert.Equal(
            LineOrder.Compare(in a, bufferA, in other, otherBuffer),
            LineOrder.Compare(in b, bufferB, in other, otherBuffer));
    }

    [Fact]
    [Trait("Case", "OC-13")]
    public void Comparing_a_leading_zero_tie_breaks_on_raw_line_bytes()
    {
        int result = CompareLines("007. Apple", "7. Apple");

        Assert.NotEqual(0, result);
        Assert.True(result < 0, "\"007. Apple\" starts with '0' (0x30), which is less than '7' (0x37).");
    }

    [Fact]
    [Trait("Case", "OC-14")]
    public void Comparing_a_tied_pair_agrees_regardless_of_argument_order_or_buffer_placement()
    {
        byte[] bufferA = "007. Apple\n"u8.ToArray();
        byte[] bufferB = "7. Apple\n"u8.ToArray();
        LineDescriptor a = DescriptorFixture.Build(bufferA)[0];
        LineDescriptor b = DescriptorFixture.Build(bufferB)[0];

        int forward = LineOrder.Compare(in a, bufferA, in b, bufferB);
        int backward = LineOrder.Compare(in b, bufferB, in a, bufferA);

        Assert.True(forward < 0);
        Assert.True(backward > 0);

        // Repeated with the same two lines placed together in one shared buffer,
        // at different offsets than above, to rule out a placement-dependent result.
        byte[] shared = "9. filler\n007. Apple\n7. Apple\n"u8.ToArray();
        List<LineDescriptor> both = DescriptorFixture.Build(shared);
        LineDescriptor sharedA = both[1];
        LineDescriptor sharedB = both[2];
        Assert.True(LineOrder.Compare(in sharedA, shared, in sharedB, shared) < 0);
    }

    [Fact]
    [Trait("Case", "OC-15")]
    public void Comparing_negative_zero_and_positive_numbers_orders_them_ascending()
    {
        byte[] buffer = "-5. Apple\n0. Apple\n5. Apple\n"u8.ToArray();
        List<LineDescriptor> descriptors = DescriptorFixture.Build(buffer);
        LineDescriptor negative = descriptors[0];
        LineDescriptor zero = descriptors[1];
        LineDescriptor positive = descriptors[2];

        Assert.True(LineOrder.Compare(in negative, buffer, in zero, buffer) < 0);
        Assert.True(LineOrder.Compare(in zero, buffer, in positive, buffer) < 0);
    }

    [Fact]
    [Trait("Case", "OC-16")]
    public void Comparing_invalid_byte_sequences_orders_by_byte_value_without_failing()
    {
        byte[] buffer = [.. "1. "u8.ToArray(), 0xFE, (byte)'\n', .. "1. "u8.ToArray(), 0xFF, (byte)'\n'];
        List<LineDescriptor> descriptors = DescriptorFixture.Build(buffer);
        LineDescriptor lower = descriptors[0];
        LineDescriptor higher = descriptors[1];

        Assert.True(LineOrder.Compare(in lower, buffer, in higher, buffer) < 0);
    }

    [Fact]
    [Trait("Case", "OC-17")]
    public void Comparing_an_explicit_sign_tie_breaks_on_raw_line_bytes()
    {
        int result = CompareLines("+5. Apple", "5. Apple");

        Assert.NotEqual(0, result);
        Assert.True(result < 0, "\"+5. Apple\" starts with '+' (0x2B), which is less than '5' (0x35).");
    }

    [Fact]
    [Trait("Case", "OC-18")]
    public void Comparing_a_short_string_part_against_a_longer_one_sharing_its_bytes_orders_the_short_one_first()
    {
        // "Ban" is under eight bytes, so its cached prefix is zero-padded; "Banana..."
        // is over eight bytes and its prefix holds eight real bytes. A proper prefix
        // still orders before its extension, cache or no cache.
        Assert.True(CompareLines("1. Ban", "1. Bananarama") < 0);
    }

    [Fact]
    [Trait("Case", "OC-19")]
    public void Comparing_string_parts_with_a_zero_byte_inside_the_first_eight_bytes_orders_by_that_byte()
    {
        byte[] lower = [.. "1. "u8.ToArray(), (byte)'A', 0x00, (byte)'B', (byte)'\n'];
        byte[] higher = [.. "1. "u8.ToArray(), (byte)'A', 0x01, (byte)'B', (byte)'\n'];
        LineDescriptor lowerLine = DescriptorFixture.Build(lower)[0];
        LineDescriptor higherLine = DescriptorFixture.Build(higher)[0];

        Assert.True(LineOrder.Compare(in lowerLine, lower, in higherLine, higher) < 0);
    }

    [Fact]
    [Trait("Case", "OC-20")]
    public void Comparing_string_parts_identical_in_their_first_eight_bytes_but_differing_after_orders_by_the_difference()
    {
        // Both share the eight-byte prefix "AAAAAAAA", so the fast path alone reports
        // equal prefixes and the comparison must still fall through to the full
        // ordinal comparison to find the difference at byte nine.
        Assert.True(CompareLines("1. AAAAAAAAX", "1. AAAAAAAAY") < 0);
    }

    [Fact]
    [Trait("Case", "OC-21")]
    public void Comparing_an_empty_string_part_against_one_beginning_with_a_zero_byte_orders_the_empty_one_first()
    {
        byte[] empty = "1. \n"u8.ToArray();
        byte[] zeroFirst = [.. "1. "u8.ToArray(), 0x00, (byte)'\n'];
        LineDescriptor emptyLine = DescriptorFixture.Build(empty)[0];
        LineDescriptor zeroFirstLine = DescriptorFixture.Build(zeroFirst)[0];

        // The empty string part's prefix is all padding (0x00 in every byte); a string
        // part starting with a real 0x00 byte has the identical all-zero prefix, so
        // this pair proves the fast path's equal-prefix case truly falls through to
        // SequenceCompareTo rather than treating "prefix all zero" as "empty".
        Assert.True(LineOrder.Compare(in emptyLine, empty, in zeroFirstLine, zeroFirst) < 0);
    }

    [Fact]
    [Trait("Case", "OC-22")]
    public void Comparing_pairs_that_tie_on_the_padded_prefix_falls_through_to_order_the_shorter_first()
    {
        // "ab" is short of eight bytes, so its cached prefix pads the remaining
        // positions with 0x00; "ab\0" has a real 0x00 in that same position, so the two
        // padded prefixes tie -- not because nothing differs between the strings, but
        // because padding and a real zero byte read identically there. Only the
        // fall-through to SequenceCompareTo can tell them apart, and it must order the
        // shorter one first, as for any proper prefix against its extension.
        Assert.True(CompareLines("1. ab", "1. ab\0") < 0);
        Assert.True(CompareLines("1. ab", "1. ab\0c") < 0);

        // The eight/nine-byte boundary itself: a string part exactly eight bytes long
        // (an all-real cached prefix, no padding at all) against its own nine-byte
        // extension, which the fast path also cannot resolve on the prefix alone.
        Assert.True(CompareLines("1. AAAAAAAA", "1. AAAAAAAAX") < 0);
    }

    [Fact]
    [Trait("Case", "OC-09")]
    public void Comparing_the_curated_sample_is_antisymmetric()
    {
        (byte[] buffer, List<LineDescriptor> lines) = CuratedSample();

        foreach (LineDescriptor x in lines)
        {
            foreach (LineDescriptor y in lines)
            {
                int forward = LineOrder.Compare(in x, buffer, in y, buffer);
                int backward = LineOrder.Compare(in y, buffer, in x, buffer);
                Assert.Equal(Math.Sign(forward), -Math.Sign(backward));
            }
        }
    }

    [Fact]
    [Trait("Case", "OC-10")]
    public void Comparing_the_curated_sample_is_transitive()
    {
        (byte[] buffer, List<LineDescriptor> lines) = CuratedSample();

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
    }

    [Fact]
    [Trait("Case", "OC-11")]
    public void Comparing_the_curated_sample_is_total()
    {
        (byte[] buffer, List<LineDescriptor> lines) = CuratedSample();

        foreach (LineDescriptor x in lines)
        {
            // Reflexivity is the sharpest failure mode a broken totality claim would
            // show here: every entry must tie with itself, or "equal" is not well defined.
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
    }

    private static int CompareLines(string lineA, string lineB)
    {
        byte[] bufferA = Encoding.UTF8.GetBytes(lineA + "\n");
        byte[] bufferB = Encoding.UTF8.GetBytes(lineB + "\n");
        LineDescriptor a = DescriptorFixture.Build(bufferA)[0];
        LineDescriptor b = DescriptorFixture.Build(bufferB)[0];
        return LineOrder.Compare(in a, bufferA, in b, bufferB);
    }

    // One buffer covering empty/non-empty, prefix, case, ASCII/non-ASCII, invalid
    // bytes, negative/zero/positive, both tie-break shapes, an exact duplicate, and --
    // to push the law tests through the prefix step in both its real and padded forms
    // -- an exactly eight-byte string part, a nine-byte extension sharing those eight
    // bytes, and a pair whose string parts contain a real 0x00 byte.
    private static (byte[] Buffer, List<LineDescriptor> Lines) CuratedSample()
    {
        List<byte> bytes = [];
        void Add(byte[] line)
        {
            bytes.AddRange(line);
            bytes.Add((byte)'\n');
        }

        Add("5. "u8.ToArray());
        Add("5. A"u8.ToArray());
        Add("1. Ban"u8.ToArray());
        Add("1. Banana"u8.ToArray());
        Add("1. apple"u8.ToArray());
        Add("1. Apple"u8.ToArray());
        Add("1. zzz"u8.ToArray());
        Add(Encoding.UTF8.GetBytes("1. café"));
        Add([(byte)'1', (byte)'.', (byte)' ', 0xFE]);
        Add([(byte)'1', (byte)'.', (byte)' ', 0xFF]);
        Add("-5. Apple"u8.ToArray());
        Add("0. Apple"u8.ToArray());
        Add("007. Apple"u8.ToArray());
        Add("7. Apple"u8.ToArray());
        Add("+5. Apple"u8.ToArray());
        Add("42. Same"u8.ToArray());
        Add("42. Same"u8.ToArray());
        Add("1. AAAAAAAA"u8.ToArray());  // string part exactly eight bytes: an all-real cached prefix
        Add("1. AAAAAAAAX"u8.ToArray()); // nine bytes, sharing the eight-byte entry's prefix exactly
        Add([(byte)'1', (byte)'.', (byte)' ', (byte)'A', 0x00, (byte)'B']); // real 0x00 inside the prefix window
        Add([(byte)'1', (byte)'.', (byte)' ', (byte)'A', 0x01, (byte)'B']); // one byte away from the entry above

        byte[] buffer = [.. bytes];
        return (buffer, DescriptorFixture.Build(buffer));
    }
}
