using System.Globalization;

namespace FileSorter.LineFormat;

internal static class LineParser
{
    private const byte Separator = (byte)'.';
    private const byte Space = (byte)' ';

    /// The caller strips terminators. stringStart is relative to the line.
    public static bool TryParse(ReadOnlySpan<byte> line, out long number, out int stringStart)
    {
        stringStart = 0;

        // The first period ends the number; later periods belong to the string.
        int separatorIndex = line.IndexOf(Separator);
        if (separatorIndex < 0)
        {
            number = 0;
            return false;
        }

        // Grammar is [+-]?[0-9]+; long.TryParse alone would also accept trailing NULs.
        // The scan is a vectorised early-exit over the field, and TryParse then does the range check.
        ReadOnlySpan<byte> field = line[..separatorIndex];
        ReadOnlySpan<byte> digits = field.Length > 0 && (field[0] is (byte)'+' or (byte)'-') ? field[1..] : field;
        if (digits.IsEmpty || digits.IndexOfAnyExceptInRange((byte)'0', (byte)'9') >= 0)
        {
            number = 0;
            return false;
        }

        if (!long.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        stringStart = separatorIndex + 1;
        if (stringStart < line.Length && line[stringStart] == Space)
        {
            stringStart++;
        }

        return true;
    }
}
