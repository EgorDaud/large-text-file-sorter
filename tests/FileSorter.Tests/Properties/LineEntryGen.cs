using System.Globalization;
using System.Text;
using CsCheck;

namespace FileSorter.Tests.Properties;

internal static class LineEntryGen
{
    // Printable ASCII keeps one char = one UTF-8 byte, so the byte-wise oracle agrees with ordinal comparison.
    private static readonly Gen<char> PrintableAsciiChar = Gen.Char[' ', '~'];

    private static readonly Gen<string> PrintableStringPart = Gen.String[PrintableAsciiChar, 0, 16];

    // Occasionally ends the string part in a '\r' that is content, not half of a terminator.
    private static readonly Gen<string> StringPart =
        Gen.Select(PrintableStringPart, Gen.Int[0, 19], (s, roll) => roll == 0 ? s + "\r" : s);

    private static readonly Gen<long> Number = Gen.Long[-1_000_000_000_000L, 1_000_000_000_000L];

    public static readonly Gen<List<(long Number, string StringPart)>> Entries =
        Gen.Select(Number, StringPart).List[0, 24];

    public static readonly Gen<(RandomLineFileGen.Terminator Terminator, bool TrailingCarriageReturn)>
        FinalTermination = RandomLineFileGen.FinalTerminatorSpec;

    // Every fifth entry gets a byte-different twin (leading zeros or '+') so the raw-byte tie-break is reached.
    public static byte[] BuildInput(
        List<(long Number, string StringPart)> entries,
        (RandomLineFileGen.Terminator Terminator, bool TrailingCarriageReturn)? finalTermination = null) =>
        Render(ComposeLines(entries), finalTermination ?? (RandomLineFileGen.Terminator.LineFeed, false));

    private static List<string> ComposeLines(List<(long Number, string StringPart)> entries)
    {
        List<string> lines = [];
        int twinCount = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            (long number, string stringPart) = entries[i];
            lines.Add(RenderLineContent(number.ToString(CultureInfo.InvariantCulture), stringPart));

            if (i % 5 == 0)
            {
                string twin = number >= 0 && twinCount % 2 == 1
                    ? "+" + number.ToString(CultureInfo.InvariantCulture)
                    : PadWithLeadingZeros(number);
                lines.Add(RenderLineContent(twin, stringPart));
                twinCount++;
            }
        }

        return lines;
    }

    // The appended '\r' is the one the terminator rule strips, leaving the original as content.
    private static string RenderLineContent(string renderedNumber, string stringPart) =>
        stringPart.EndsWith('\r') ? $"{renderedNumber}. {stringPart}\r" : $"{renderedNumber}. {stringPart}";

    private static byte[] Render(
        List<string> lines, (RandomLineFileGen.Terminator Terminator, bool TrailingCarriageReturn) finalTermination)
    {
        StringBuilder text = new();
        for (int i = 0; i < lines.Count; i++)
        {
            text.Append(lines[i]);
            if (i < lines.Count - 1)
            {
                text.Append('\n');
                continue;
            }

            switch (finalTermination.Terminator)
            {
                case RandomLineFileGen.Terminator.LineFeed:
                    text.Append('\n');
                    break;
                case RandomLineFileGen.Terminator.CarriageReturnLineFeed:
                    text.Append('\r').Append('\n');
                    break;
                case RandomLineFileGen.Terminator.None when finalTermination.TrailingCarriageReturn:
                    text.Append('\r');
                    break;
                case RandomLineFileGen.Terminator.None:
                    break;
            }
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    // Not via Math.Abs, which overflows on long.MinValue.
    private static string PadWithLeadingZeros(long number)
    {
        string canonical = number.ToString(CultureInfo.InvariantCulture);
        return canonical.StartsWith('-') ? "-00" + canonical[1..] : "00" + canonical;
    }
}
