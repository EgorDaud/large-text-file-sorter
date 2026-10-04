using System.Globalization;

namespace FileSorter.LineFormat;

internal static class LineParser
{
    private const byte Separator = (byte)'.';
    private const byte Space = (byte)' ';

    public static bool TryParse(ReadOnlySpan<byte> line, out long number, out int stringStart)
    {
        stringStart = 0;

        int separatorIndex = line.IndexOf(Separator);
        if (separatorIndex < 0)
        {
            number = 0;
            return false;
        }

        // long.TryParse accepts trailing NULs; requiring a final digit rejects them.
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
