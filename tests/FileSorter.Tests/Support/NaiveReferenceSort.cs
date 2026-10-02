using System.Globalization;
using System.Text;

namespace FileSorter.Tests.Support;

// Deliberately shares no code with FileSorter.LineFormat, so a comparator defect cannot pass on both sides.
internal static class NaiveReferenceSort
{
    public static readonly IComparer<string> LineComparer = Comparer<string>.Create(CompareLines);

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

    private static List<(int Offset, int Length)> SplitLines(byte[] input)
    {
        NaiveLineFormat.NaiveSplitResult result =
            NaiveLineFormat.Split(input, maxLineLength: int.MaxValue, stripBom: true, stripCarriageReturn: true);

        return [.. result.Lines.Select(line => ((int)line.Offset, line.Length))];
    }

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
