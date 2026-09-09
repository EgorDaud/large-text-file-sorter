using System.Globalization;
using System.Text;
using CsCheck;

namespace FileSorter.Tests.Properties;

/// <summary>
/// Random line generation shared by the property tests: a valid file built from parts a
/// test can reason about independently of the sorter.
/// </summary>
internal static class LineEntryGen
{
    // Printable ASCII only (space through '~'), so one .NET char is always one UTF-8
    // byte and the oracle's byte-wise comparison cannot disagree with .NET's ordinal
    // string comparison over a surrogate pair.
    private static readonly Gen<char> PrintableAsciiChar = Gen.Char[' ', '~'];

    private static readonly Gen<string> PrintableStringPart = Gen.String[PrintableAsciiChar, 0, 16];

    // One roll in twenty ends the string part in a '\r' that is content, not the first
    // half of a terminator; BuildInput writes the second '\r' that keeps it so.
    private static readonly Gen<string> StringPart =
        Gen.Select(PrintableStringPart, Gen.Int[0, 19], (s, roll) => roll == 0 ? s + "\r" : s);

    // A moderate rather than full-range spread: sign and magnitude variety is what
    // these properties need, and the extreme-value boundaries are already unit cases.
    private static readonly Gen<long> Number = Gen.Long[-1_000_000_000_000L, 1_000_000_000_000L];

    // Up to 24 entries forces several chunks at the small budgets these properties use,
    // while keeping each iteration -- a real file written, sorted, and read back -- fast
    // enough to run in the hundreds.
    public static readonly Gen<List<(long Number, string StringPart)>> Entries =
        Gen.Select(Number, StringPart).List[0, 24];

    // The file's last line's own terminator: ordinary '\n', explicit '\r\n', or none at
    // all -- and, only in the "none" case, an extra trailing '\r' left with no '\n'
    // behind it. Reuses RandomLineFileGen's model of exactly these four end-of-file
    // shapes rather than building a second one; every non-final line stays plain '\n',
    // since only a file's last line can be unterminated or end in a bare '\r' at all.
    public static readonly Gen<(RandomLineFileGen.Terminator Terminator, bool TrailingCarriageReturn)>
        FinalTermination = RandomLineFileGen.FinalTerminatorSpec;

    // Builds file bytes from generated entries, every line but the last terminated by an
    // ordinary '\n'. Every fifth entry gets a twin -- identical parsed number, identical
    // string part, different raw bytes -- because random numbers and strings essentially
    // never collide on both keys by chance and the third comparison level would
    // otherwise never be reached. The twin alternates between the two raw-byte-tie
    // mechanisms, leading zeros and an explicit '+', so both routes to that level are
    // exercised.
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
                // A negative number has no explicit-plus form, so it always takes the
                // zero-padded twin instead of a turn in the alternation.
                string twin = number >= 0 && twinCount % 2 == 1
                    ? "+" + number.ToString(CultureInfo.InvariantCulture)
                    : PadWithLeadingZeros(number);
                lines.Add(RenderLineContent(twin, stringPart));
                twinCount++;
            }
        }

        return lines;
    }

    // A line's own content: number, ". ", string part. A string part already ending in
    // '\r' gets a second one appended here, unconditionally and regardless of where this
    // line ends up. For an ordinary next-line '\n', a final "\r\n", or a final bare '\r'
    // with nothing behind it, exactly one of the two is stripped as that terminator's own
    // other half, leaving the original single '\r' as content -- which is the point. The
    // one place this over-applies: the last line, when the final termination is "none"
    // plus its own trailing carriage return. There, Render already appends a bare '\r' of
    // its own to serve as the terminator the end-of-file rule strips, so this line ends
    // up keeping two literal '\r' bytes of content rather than one; still well-formed,
    // just not the single '\r' the rest of this comment describes.
    private static string RenderLineContent(string renderedNumber, string stringPart) =>
        stringPart.EndsWith('\r') ? $"{renderedNumber}. {stringPart}\r" : $"{renderedNumber}. {stringPart}";

    // Joins composed lines with '\n' throughout, except the very last, which takes
    // whatever finalTermination names -- the same four shapes RandomLineFileGen builds
    // for the splitter sweeps, applied here to a whole parsed, sorted file instead of a
    // raw byte stream.
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

    // Two extra leading zeros after any sign: numerically inert, so only the raw bytes
    // differ. Built from the canonical string rather than through Math.Abs, which
    // overflows on long.MinValue.
    private static string PadWithLeadingZeros(long number)
    {
        string canonical = number.ToString(CultureInfo.InvariantCulture);
        return canonical.StartsWith('-') ? "-00" + canonical[1..] : "00" + canonical;
    }
}
