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

        if (!long.TryParse(line[..separatorIndex], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number))
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
