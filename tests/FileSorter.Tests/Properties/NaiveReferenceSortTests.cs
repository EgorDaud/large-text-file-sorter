using Xunit;

namespace FileSorter.Tests.Properties;

/// <summary>
/// Deterministic cases for <see cref="NaiveReferenceSort"/>'s own end-of-file handling,
/// pinned directly against the oracle rather than through a generated property: each
/// case below is one specific structural shape the documented input rule names, not a
/// random one.
/// </summary>
public sealed class NaiveReferenceSortTests
{
    [Fact]
    [Trait("Case", "PB-15")]
    public void Sorting_an_unterminated_final_line_keeps_it_as_a_whole_line()
    {
        byte[] input = "2. Banana\n1. Apple"u8.ToArray();

        byte[] actual = NaiveReferenceSort.Sort(input);

        Assert.Equal("1. Apple\n2. Banana\n"u8.ToArray(), actual);
    }

    [Fact]
    [Trait("Case", "PB-16")]
    public void Sorting_a_final_bare_carriage_return_strips_it_as_the_terminators_other_half()
    {
        byte[] input = "2. Banana\n1. Apple\r"u8.ToArray();

        byte[] actual = NaiveReferenceSort.Sort(input);

        Assert.Equal("1. Apple\n2. Banana\n"u8.ToArray(), actual);
    }

    [Fact]
    [Trait("Case", "PB-17")]
    public void Sorting_a_lone_carriage_return_tail_after_the_last_terminated_line_leaves_no_extra_line()
    {
        // The last '\n' ends "2. Banana"; the single '\r' after it has nothing behind it
        // and nothing of its own, so it is an empty tail, not a one-byte third line.
        byte[] input = "1. Apple\n2. Banana\n\r"u8.ToArray();

        byte[] actual = NaiveReferenceSort.Sort(input);

        Assert.Equal("1. Apple\n2. Banana\n"u8.ToArray(), actual);
    }

    [Fact]
    [Trait("Case", "PB-18")]
    public void Sorting_bom_only_input_produces_empty_output()
    {
        byte[] input = [0xEF, 0xBB, 0xBF];

        byte[] actual = NaiveReferenceSort.Sort(input);

        Assert.Empty(actual);
    }

    [Fact]
    [Trait("Case", "PB-19")]
    public void Sorting_a_single_bare_carriage_return_produces_empty_output()
    {
        // LineEntryGen never draws this shape on its own -- Render only applies the
        // final termination when there is at least one entry, so a zero-entry file
        // never picks up a trailing '\r'. Pinned here instead: the same tail rule
        // PB-17 checks with content ahead of it applies with nothing ahead of it
        // either -- the lone '\r' is the first half of a terminator whose '\n' never
        // arrived, and a tail of nothing else is an empty tail, not a one-byte line.
        byte[] input = "\r"u8.ToArray();

        byte[] actual = NaiveReferenceSort.Sort(input);

        Assert.Empty(actual);
    }
}
