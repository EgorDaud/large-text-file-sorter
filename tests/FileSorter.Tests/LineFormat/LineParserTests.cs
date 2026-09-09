using FileSorter.LineFormat;
using Xunit;

namespace FileSorter.Tests.LineFormat;

public sealed class LineParserTests
{
    [Fact]
    [Trait("Case", "LP-01")]
    public void Parsing_a_well_formed_line_splits_it_into_number_and_string_extent()
    {
        AssertParses("415. Apple"u8, 415, "Apple");
    }

    [Fact]
    [Trait("Case", "LP-02")]
    public void Parsing_a_line_whose_string_part_contains_a_period_and_a_space_splits_on_the_first_period()
    {
        AssertParses("1. Apple. Banana is yellow"u8, 1, "Apple. Banana is yellow");
    }

    [Fact]
    [Trait("Case", "LP-03")]
    public void Parsing_a_line_with_no_space_after_the_period_still_parses()
    {
        AssertParses("7.Apple"u8, 7, "Apple");
    }

    [Fact]
    [Trait("Case", "LP-04")]
    public void Parsing_a_line_whose_string_part_begins_with_a_period_keeps_the_period()
    {
        AssertParses("7. . leading dot"u8, 7, ". leading dot");
    }

    [Fact]
    [Trait("Case", "LP-05")]
    public void Parsing_a_line_with_two_spaces_after_the_period_consumes_only_one()
    {
        AssertParses("3.  two spaces before the text"u8, 3, " two spaces before the text");
    }

    [Fact]
    [Trait("Case", "LP-06")]
    public void Parsing_a_line_with_no_period_is_malformed()
    {
        AssertMalformed("415 Apple"u8);
    }

    [Fact]
    [Trait("Case", "LP-07")]
    public void Parsing_a_line_whose_prefix_is_not_numeric_is_malformed()
    {
        AssertMalformed("abc. Apple"u8);
    }

    [Fact]
    [Trait("Case", "LP-08")]
    public void Parsing_a_line_whose_prefix_is_partly_numeric_is_malformed()
    {
        AssertMalformed("12x. Apple"u8);
    }

    [Fact]
    [Trait("Case", "LP-09")]
    public void Parsing_a_line_with_an_empty_string_part_accepts_it()
    {
        AssertParses("42. "u8, 42, "");
    }

    [Fact]
    [Trait("Case", "LP-10")]
    public void Parsing_a_line_ending_immediately_after_the_period_accepts_an_empty_string_part()
    {
        AssertParses("88."u8, 88, "");
    }

    [Fact]
    [Trait("Case", "LP-11")]
    public void Parsing_a_line_with_leading_zeros_reports_the_numeric_value()
    {
        AssertParses("007. Apple"u8, 7, "Apple");
    }

    [Fact]
    [Trait("Case", "LP-12")]
    public void Parsing_the_extreme_representable_values_parses_them_exactly()
    {
        AssertParses("9223372036854775807. Apple"u8, long.MaxValue, "Apple");
        AssertParses("-9223372036854775808. Apple"u8, long.MinValue, "Apple");
    }

    [Fact]
    [Trait("Case", "LP-13")]
    public void Parsing_values_one_step_beyond_each_extreme_is_malformed()
    {
        AssertMalformed("9223372036854775808. Apple"u8);
        AssertMalformed("-9223372036854775809. Apple"u8);
    }

    [Fact]
    [Trait("Case", "LP-14")]
    public void Parsing_a_negative_number_accepts_it()
    {
        AssertParses("-5. Apple"u8, -5, "Apple");
    }

    [Fact]
    [Trait("Case", "LP-15")]
    public void Parsing_a_lone_sign_with_no_digits_is_malformed()
    {
        AssertMalformed("-. Apple"u8);
    }

    [Fact]
    [Trait("Case", "LP-16")]
    public void Parsing_zero_accepts_it_as_the_number()
    {
        AssertParses("0. Apple"u8, 0, "Apple");
    }

    [Fact]
    [Trait("Case", "LP-17")]
    public void Parsing_a_line_with_trailing_whitespace_preserves_it_verbatim()
    {
        AssertParses("3. spaced out   "u8, 3, "spaced out   ");
    }

    [Fact]
    [Trait("Case", "LP-18")]
    public void Parsing_a_line_that_is_only_a_period_is_malformed()
    {
        AssertMalformed("."u8);
    }

    [Fact]
    [Trait("Case", "LP-19")]
    public void Parsing_a_completely_empty_line_is_malformed()
    {
        AssertMalformed([]);
    }

    [Fact]
    [Trait("Case", "LP-20")]
    public void Parsing_a_line_of_digits_with_no_period_is_malformed()
    {
        AssertMalformed("12345"u8);
    }

    [Fact]
    [Trait("Case", "LP-21")]
    public void Parsing_a_line_with_multi_byte_content_preserves_the_bytes_undecoded()
    {
        byte[] line = [.. "3. "u8.ToArray(), .. "café"u8.ToArray()];

        AssertParses(line, 3, "café"u8.ToArray());
    }

    [Fact]
    [Trait("Case", "LP-22")]
    public void Parsing_a_line_with_invalid_utf8_bytes_passes_them_through_without_validating()
    {
        byte[] line = [(byte)'1', (byte)'.', (byte)' ', 0xFF, 0xFE, 0x00];

        Assert.True(LineParser.TryParse(line, out long number, out int stringStart));
        Assert.Equal(1, number);
        Assert.Equal(line[3..], line[stringStart..].ToArray());
    }

    // There is no test here for the operator-facing diagnostic: TryParse returns false
    // and carries no position, because the caller is what knows the byte offset and the
    // line number. ChunkReader owns that diagnostic and is tested for it.

    private static void AssertParses(ReadOnlySpan<byte> line, long expectedNumber, string expectedStringPart)
    {
        AssertParses(line, expectedNumber, System.Text.Encoding.UTF8.GetBytes(expectedStringPart));
    }

    private static void AssertParses(ReadOnlySpan<byte> line, long expectedNumber, byte[] expectedStringPart)
    {
        Assert.True(LineParser.TryParse(line, out long number, out int stringStart));
        Assert.Equal(expectedNumber, number);
        Assert.Equal(expectedStringPart, line[stringStart..].ToArray());
    }

    private static void AssertMalformed(ReadOnlySpan<byte> line)
    {
        Assert.False(LineParser.TryParse(line, out _, out _));
    }
}
