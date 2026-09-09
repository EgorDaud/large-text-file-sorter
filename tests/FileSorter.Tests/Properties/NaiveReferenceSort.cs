using System.Globalization;
using System.Text;

namespace FileSorter.Tests.Properties;

/// <summary>
/// The byte-identity oracle. It deliberately shares no code with the production
/// comparator -- it references <c>FileSorter.LineFormat</c> nowhere -- because a defect
/// shared between the two would produce identical wrong output on both sides and the
/// property would pass while proving nothing. Every rule below is re-derived from the
/// behavioural contract rather than copied from a production type.
/// </summary>
internal static class NaiveReferenceSort
{
    /// The same three-level order as <see cref="Compare"/>, over whole lines given as
    /// ASCII strings rather than as offsets into one buffer, for tests that build their
    /// fixtures out of lines and have to decide whether one slice is entirely below
    /// another without asking the production comparator.
    public static readonly IComparer<string> LineComparer = Comparer<string>.Create(CompareLines);

    // Splits the input into lines, parses each line's key, sorts by the three-level
    // order, and writes every line back out with a single '\n', including the last.
    // Small inputs only: the whole file and every line's key are held in memory.
    public static byte[] Sort(byte[] input)
    {
        List<(int Offset, int Length)> lines = SplitLines(input);
        Entry[] entries = new Entry[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            (int offset, int length) = lines[i];
            (long number, int stringOffset, int stringLength) = ParseIndependently(input, offset, length);
            entries[i] = new Entry(number, stringOffset, stringLength, offset, length);
        }

        Array.Sort(entries, (a, b) => Compare(input, a, b));

        using MemoryStream output = new();
        foreach (Entry entry in entries)
        {
            output.Write(input, entry.LineOffset, entry.LineLength);
            output.WriteByte((byte)'\n');
        }

        return output.ToArray();
    }

    // A leading byte order mark is consumed once, at offset zero, and is never part of
    // a line. The empty region after the file's final terminator is not a line. Exactly
    // one carriage return immediately before a line feed is stripped; any other
    // carriage return is ordinary content. A bare carriage return at the very end of the
    // file, with no line feed after it, is the first half of a terminator whose line
    // feed never arrived, and is stripped the same way; a trailing region that is only
    // that one byte is an empty tail, not a one-byte line.
    //
    // Delegates the actual splitting to NaiveLineFormat, the splitter oracle the
    // buffer-boundary sweeps already check LineCursor against: writing this file's own
    // second, slightly different reading of the same end-of-file rule risks the two
    // oracles drifting apart from each other rather than from production. This call
    // still shares no code with LineCursor itself. This oracle takes whole in-memory
    // inputs with no line-length ceiling of its own, so it passes int.MaxValue and
    // never sees NaiveLineFormat's malformed result -- that path belongs to the callers
    // that pass a real limit (the buffer-boundary sweeps).
    private static List<(int Offset, int Length)> SplitLines(byte[] input)
    {
        NaiveLineFormat.NaiveSplitResult result =
            NaiveLineFormat.Split(input, maxLineLength: int.MaxValue, stripBom: true, stripCarriageReturn: true);

        return [.. result.Lines.Select(line => ((int)line.Offset, line.Length))];
    }

    // The boundary is the first '.' in the line; one space immediately after it is
    // consumed if present, and the string part is everything following, verbatim.
    private static (long Number, int StringOffset, int StringLength) ParseIndependently(
        byte[] buffer, int offset, int length)
    {
        ReadOnlySpan<byte> line = buffer.AsSpan(offset, length);
        int separator = line.IndexOf((byte)'.');
        long number = long.Parse(
            Encoding.ASCII.GetString(line[..separator]), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        int stringStart = separator + 1;
        if (stringStart < line.Length && line[stringStart] == (byte)' ')
        {
            stringStart++;
        }

        return (number, offset + stringStart, length - stringStart);
    }

    // String part ascending, ordinal over raw bytes; then number ascending; then the
    // raw line bytes ascending as the final deterministic tie-break.
    private static int Compare(byte[] buffer, Entry a, Entry b)
    {
        int stringComparison = buffer.AsSpan(a.StringOffset, a.StringLength)
            .SequenceCompareTo(buffer.AsSpan(b.StringOffset, b.StringLength));
        if (stringComparison != 0)
        {
            return stringComparison;
        }

        int numberComparison = a.Number.CompareTo(b.Number);
        if (numberComparison != 0)
        {
            return numberComparison;
        }

        return buffer.AsSpan(a.LineOffset, a.LineLength).SequenceCompareTo(buffer.AsSpan(b.LineOffset, b.LineLength));
    }

    private static int CompareLines(string? a, string? b)
    {
        byte[] left = Encoding.ASCII.GetBytes(a ?? string.Empty);
        byte[] right = Encoding.ASCII.GetBytes(b ?? string.Empty);
        (long leftNumber, int leftStringOffset, int leftStringLength) = ParseIndependently(left, 0, left.Length);
        (long rightNumber, int rightStringOffset, int rightStringLength) = ParseIndependently(right, 0, right.Length);

        int stringComparison = left.AsSpan(leftStringOffset, leftStringLength)
            .SequenceCompareTo(right.AsSpan(rightStringOffset, rightStringLength));
        if (stringComparison != 0)
        {
            return stringComparison;
        }

        int numberComparison = leftNumber.CompareTo(rightNumber);
        return numberComparison != 0 ? numberComparison : left.AsSpan().SequenceCompareTo(right);
    }

    private readonly record struct Entry(long Number, int StringOffset, int StringLength, int LineOffset, int LineLength);
}
