using System.Globalization;

namespace Shared;

internal static class ByteSize
{
    private static readonly string[] SizeNames = ["B", "KiB", "MiB", "GiB"];

    public static bool TryParse(string text, out long bytes)
    {
        bytes = 0;
        long multiplier = 1;
        ReadOnlySpan<char> digits = text;

        // Match the largest unit first so "B" cannot shadow the longer suffixes.
        for (int name = SizeNames.Length - 1; name >= 0; name--)
        {
            if (text.EndsWith(SizeNames[name], StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1L << (10 * name);
                digits = text.AsSpan(0, text.Length - SizeNames[name].Length);
                break;
            }
        }

        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            || value > long.MaxValue / multiplier)
        {
            return false;
        }

        bytes = value * multiplier;
        return true;
    }

    public static string Describe(long bytes)
    {
        double value = bytes;
        int name = 0;

        // Round before choosing a unit so near-boundary values read as 1.0 GiB.
        while (Math.Round(value, 1) >= 1024 && name < SizeNames.Length - 1)
        {
            value /= 1024;
            name++;
        }

        return name == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:F1} {SizeNames[name]}");
    }
}
