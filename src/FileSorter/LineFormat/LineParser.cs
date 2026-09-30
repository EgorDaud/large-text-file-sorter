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

        // Grammar is [+-]?[0-9]+. With these styles long.TryParse enforces all of it except
        // that it ignores trailing NULs, so a final digit closes the gap without a second scan.
        ReadOnlySpan<byte> field = line[..separatorIndex];
        if (!long.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number)
            || !char.IsAsciiDigit((char)field[^1]))
        {
            number = 0;
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
