using System.Text;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using FileSorter.Tests.LineFormat;
using Xunit;

namespace FileSorter.Tests.RunGeneration;

public sealed class ChunkSorterTests
{
    [Fact]
    [Trait("Case", "CS-01")]
    public void Sorting_an_already_sorted_chunk_leaves_it_in_order()
    {
        byte[] buffer = "1. Apple\n2. Banana\n3. Cherry\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["1. Apple", "2. Banana", "3. Cherry"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-02")]
    public void Sorting_a_reverse_ordered_chunk_produces_ascending_order()
    {
        byte[] buffer = "3. Cherry\n2. Banana\n1. Apple\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["1. Apple", "2. Banana", "3. Cherry"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-03")]
    public void Sorting_a_chunk_of_identical_lines_changes_nothing_observable()
    {
        byte[] buffer = "9. Same\n9. Same\n9. Same\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["9. Same", "9. Same", "9. Same"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-04")]
    public void Sorting_a_chunk_with_one_shared_string_part_orders_by_number_then_raw_bytes()
    {
        byte[] buffer = "5. Apple\n-3. Apple\n007. Apple\n7. Apple\n0. Apple\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["-3. Apple", "0. Apple", "5. Apple", "007. Apple", "7. Apple"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-05")]
    public void Sorting_a_single_line_chunk_is_a_no_op()
    {
        byte[] buffer = "42. Only\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["42. Only"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-06")]
    public void Sorting_an_empty_chunk_does_not_throw()
    {
        LineDescriptor[] descriptors = [];

        ChunkSorter.Sort(descriptors, []);

        Assert.Empty(descriptors);
    }

    [Fact]
    [Trait("Case", "CS-07")]
    public void Sorting_preserves_the_multiset_of_lines()
    {
        byte[] buffer = "3. Cherry\n1. Apple\n1. Apple\n2. Banana\n3. Cherry\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);
        string[] before = ReadBack(descriptors, buffer);

        ChunkSorter.Sort(descriptors, buffer);

        string[] after = ReadBack(descriptors, buffer);
        Assert.Equal(before.OrderBy(x => x, StringComparer.Ordinal), after.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(before.Length, after.Length);
    }

    [Fact]
    [Trait("Case", "CS-08")]
    public void Sorting_produces_the_same_order_across_repeated_runs()
    {
        byte[] buffer = "9. filler\n007. Apple\n7. Apple\n1. Zebra\n"u8.ToArray();

        LineDescriptor[] runOne = Build(buffer);
        ChunkSorter.Sort(runOne, buffer);
        string[] orderOne = ReadBack(runOne, buffer);

        LineDescriptor[] runTwo = Build(buffer);
        ChunkSorter.Sort(runTwo, buffer);
        string[] orderTwo = ReadBack(runTwo, buffer);

        Assert.Equal(orderOne, orderTwo);
    }

    [Fact]
    [Trait("Case", "CS-08")]
    public void Sorting_the_same_leading_zero_tie_agrees_regardless_of_which_chunk_it_lands_in()
    {
        byte[] chunkA = "9. filler\n007. Apple\n7. Apple\n"u8.ToArray();
        byte[] chunkB = "1. Zebra\n2. Banana\n007. Apple\n7. Apple\n3. Cherry\n"u8.ToArray();

        LineDescriptor[] descriptorsA = Build(chunkA);
        LineDescriptor[] descriptorsB = Build(chunkB);
        ChunkSorter.Sort(descriptorsA, chunkA);
        ChunkSorter.Sort(descriptorsB, chunkB);

        Assert.Equal("007. Apple", WinnerOfTie(descriptorsA, chunkA));
        Assert.Equal("007. Apple", WinnerOfTie(descriptorsB, chunkB));
    }

    [Fact]
    [Trait("Case", "CS-09")]
    public void Sorting_a_chunk_where_every_string_part_shares_a_top_byte_still_orders_correctly()
    {
        byte[] buffer = "1. Az\n2. Apple\n3. Avocado\n4. Apricot\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["2. Apple", "4. Apricot", "3. Avocado", "1. Az"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-10")]
    public void Sorting_a_chunk_with_empty_string_parts_puts_them_first()
    {
        byte[] buffer = "3. Zebra\n2. \n5. Apple\n1. \n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["1. ", "2. ", "5. Apple", "3. Zebra"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-11")]
    public void Sorting_strings_sharing_a_top_byte_but_differing_later_orders_by_the_later_bytes()
    {
        byte[] buffer = "1. Ab\n2. Aa\n3. Ac\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["2. Aa", "1. Ab", "3. Ac"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-12")]
    public void Sorting_strings_whose_full_eight_byte_prefix_ties_still_orders_by_the_bytes_after_it()
    {
        byte[] buffer = "1. AAAAAAAAY\n2. AAAAAAAAX\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["2. AAAAAAAAX", "1. AAAAAAAAY"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-13")]
    public void Sorting_string_parts_shorter_than_eight_bytes_orders_by_the_zero_padded_prefix()
    {
        byte[] buffer = "1. Z\n2. AB\n3. A\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["3. A", "2. AB", "1. Z"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-14")]
    public void Sorting_string_parts_that_agree_for_eight_bytes_orders_by_the_ninth_and_tenth()
    {
        const string Filler = "4. " + Head + "zz";

        string[] sorted = SortLatin1(["1. " + Head + "AB", "2. " + Head + "AA", "3. " + Head + "B"], Filler);

        Assert.Equal(
            Expected(Filler, "2. " + Head + "AA", "1. " + Head + "AB", "3. " + Head + "B"),
            sorted);
    }

    [Fact]
    [Trait("Case", "CS-15")]
    public void Sorting_a_string_part_of_exactly_eight_bytes_puts_it_before_its_own_extension()
    {
        const string Filler = "3. " + Head + "zz";

        string[] sorted = SortLatin1(["1. " + Head + "A", "2. " + Head], Filler);

        Assert.Equal(Expected(Filler, "2. " + Head, "1. " + Head + "A"), sorted);
    }

    [Fact]
    [Trait("Case", "CS-16")]
    public void Sorting_a_string_part_whose_ninth_byte_is_a_real_zero_still_puts_the_shorter_one_first()
    {
        const string Filler = "4. " + Head + "zz";

        string[] sorted = SortLatin1(["1. " + Head + "A", "2. " + Head + "\0", "3. " + Head], Filler);

        Assert.Equal(
            Expected(Filler, "3. " + Head, "2. " + Head + "\0", "1. " + Head + "A"),
            sorted);
    }

    [Fact]
    [Trait("Case", "CS-17")]
    public void Sorting_string_parts_that_differ_only_in_trailing_zero_bytes_orders_the_shortest_first()
    {
        const string Filler = "4. ab\0\0\0\0\0\0z";

        string[] sorted = SortLatin1(["1. ab\0\0", "2. ab", "3. ab\0"], Filler);

        Assert.Equal(Expected(Filler, "2. ab", "3. ab\0", "1. ab\0\0"), sorted);
    }

    [Fact]
    [Trait("Case", "CS-18")]
    public void Sorting_string_parts_that_agree_past_the_depth_cap_still_orders_by_the_bytes_after_it()
    {
        // Twenty shared bytes is past the radix's depth cap, so the introsort finishes this range.
        const string Twenty = "ABCDEFGHIJKLMNOPQRST";
        const string Filler = "4. " + Twenty + "z";

        string[] sorted = SortLatin1(["1. " + Twenty + "B", "2. " + Twenty + "A", "3. " + Twenty + "C"], Filler);

        Assert.Equal(
            Expected(Filler, "2. " + Twenty + "A", "1. " + Twenty + "B", "3. " + Twenty + "C"),
            sorted);
    }

    [Fact]
    [Trait("Case", "CS-19")]
    public void Sorting_string_parts_that_share_twelve_bytes_orders_by_the_thirteenth()
    {
        const string Twelve = "SHAREDPREFIX";
        const string Filler = "4. " + Twelve + "z";

        string[] sorted = SortLatin1(["1. " + Twelve + "B", "2. " + Twelve + "A", "3. " + Twelve + "C"], Filler);

        Assert.Equal(
            Expected(Filler, "2. " + Twelve + "A", "1. " + Twelve + "B", "3. " + Twelve + "C"),
            sorted);
    }

    [Fact]
    [Trait("Case", "CS-20")]
    public void Sorting_empty_string_parts_among_long_ones_still_puts_them_first()
    {
        const string Long = "LONGSTRINGPART";
        const string Filler = "4. " + Long + "z";

        string[] sorted = SortLatin1(["1. ", "2. " + Long + "A", "3. "], Filler);

        Assert.Equal(Expected(Filler, "1. ", "3. ", "2. " + Long + "A"), sorted);
    }

    // Exactly the eight bytes the cached prefix covers.
    private const string Head = "SEAMHEAD";

    // Ranges of 32 or fewer skip the radix; the filler shares the cases' leading bytes and sorts last.
    private const int FillerLines = 40;

    // Latin-1 so a string part can carry any byte value, 0x00 included.
    private static string[] SortLatin1(string[] lines, string fillerLine)
    {
        string[] all = [.. lines, .. Enumerable.Repeat(fillerLine, FillerLines)];
        byte[] buffer = Encoding.Latin1.GetBytes(string.Join('\n', all) + "\n");
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        return [.. descriptors.Select(d => Encoding.Latin1.GetString(buffer, d.Offset, d.Length))];
    }

    private static string[] Expected(string fillerLine, params string[] orderedLines) =>
        [.. orderedLines, .. Enumerable.Repeat(fillerLine, FillerLines)];

    private static string WinnerOfTie(LineDescriptor[] sorted, byte[] buffer)
    {
        string[] lines = ReadBack(sorted, buffer);
        return Array.Find(lines, line => line is "007. Apple" or "7. Apple")!;
    }

    private static LineDescriptor[] Build(byte[] buffer) => [.. DescriptorFixture.Build(buffer)];

    private static string[] ReadBack(LineDescriptor[] descriptors, byte[] buffer) =>
        [.. descriptors.Select(d => Encoding.UTF8.GetString(buffer, d.Offset, d.Length))];
}
