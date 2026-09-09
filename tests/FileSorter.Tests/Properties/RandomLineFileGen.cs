using CsCheck;

namespace FileSorter.Tests.Properties;

/// <summary>
/// Shared random-file building blocks for the two boundary sweeps: random line lengths
/// including some at exactly the limit, mixed terminators, an occasional BOM, an
/// occasional bare carriage return at end of file, and an over-length injection -- built
/// once here rather than twice, slightly differently, in each test class.
/// </summary>
internal static class RandomLineFileGen
{
    public const int MaxLineLength = 48;

    // A uniform draw over [0, 3000] puts the median generated file at roughly 1500
    // lines, tens of KiB, against the sweeps' buffer and window sizes of a little over
    // 2 KiB, so most iterations span several fills while the low end still covers 0, 1
    // and a handful of lines. A much smaller cap makes most files fit one buffer, which
    // is the coverage a single-shot test already has. Small enough that both sweeps stay
    // under about a second at 300 iterations.
    public const int SweepMaxLineCount = 3000;

    public enum Terminator { LineFeed, CarriageReturnLineFeed, None }

    public sealed record LineSpec(byte[] Content, Terminator Terminator);

    // A carriage return is deliberately allowed in content, so the sweep reaches lines
    // that end in '\r' and lines with a '\r' in the middle; stripCarriageReturn on the
    // reader and on the oracle is what decides how the resulting bytes read. '\n' stays
    // excluded: embedding one would silently create a second line that this generator's
    // LineSpec model cannot describe.
    private static readonly Gen<byte> ContentByte =
        Gen.Byte[0, 255].Where(b => b is not (byte)'\n');

    // One roll in ten pins the length at exactly MaxLineLength, which a uniform draw
    // over a 47-wide range would reach only rarely, and that boundary is the point.
    // Never below 2: the shortest grammatically valid line is one digit and a period,
    // and this sweep targets the splitter's byte boundaries, not the grammar edges.
    private static readonly Gen<int> WellFormedLength =
        Gen.Int[0, 9].SelectMany(roll => roll == 0 ? Gen.Const(MaxLineLength) : Gen.Int[2, MaxLineLength - 1]);

    public static readonly Gen<byte[]> WellFormedContent =
        WellFormedLength.SelectMany(length => ContentByte.Array[length - 2].Select(BuildContent));

    public static readonly Gen<Terminator> NonFinalTerminator =
        Gen.Bool.Select(crlf => crlf ? Terminator.CarriageReturnLineFeed : Terminator.LineFeed);

    // Only the file's last line can be unterminated or carry a bare trailing carriage
    // return: a CR with no LF after it anywhere is the first half of a "\r\n" whose LF
    // never arrived, which can only happen where the file ends. Public: LineEntryGen
    // reuses this exact model rather than building a second one of the same four shapes.
    public static readonly Gen<(Terminator Terminator, bool TrailingCarriageReturn)> FinalTerminatorSpec =
        Gen.Select(Gen.Int[0, 2], Gen.Bool, (n, trailingCr) => (
            n switch
            {
                0 => Terminator.LineFeed,
                1 => Terminator.CarriageReturnLineFeed,
                _ => Terminator.None,
            },
            trailingCr));

    public static readonly Gen<LineSpec> NonFinalLine =
        Gen.Select(WellFormedContent, NonFinalTerminator, (content, terminator) => new LineSpec(content, terminator));

    public static readonly Gen<LineSpec> FinalLine =
        Gen.Select(WellFormedContent, FinalTerminatorSpec, (content, spec) =>
        {
            byte[] finalContent = spec.Terminator == Terminator.None && spec.TrailingCarriageReturn
                ? [.. content, (byte)'\r']
                : content;
            return new LineSpec(finalContent, spec.Terminator);
        });

    public static Gen<List<LineSpec>> LineList(int maxCount) =>
        Gen.Int[0, maxCount].SelectMany(n => n == 0
            ? Gen.Const(new List<LineSpec>())
            : Gen.Select(NonFinalLine.List[n - 1], FinalLine, (List<LineSpec> init, LineSpec last) => (List<LineSpec>)[.. init, last]));

    public static byte[] BuildContent(byte[] filler)
    {
        byte[] content = new byte[filler.Length + 2];
        content[0] = (byte)'0';
        content[1] = (byte)'.';
        filler.AsSpan().CopyTo(content.AsSpan(2));
        return content;
    }

    /// Fixed filler, unlike <see cref="BuildContent"/>'s generated one: an injected
    /// over-length line only needs to be long, and its grammar is never parsed, because
    /// LineCursor's length check throws before Describe is reached.
    public static byte[] BuildOverLongContent(int length)
    {
        byte[] content = new byte[length];
        content[0] = (byte)'0';
        content[1] = (byte)'.';
        Array.Fill(content, (byte)'x', 2, length - 2);
        return content;
    }

    public static byte[] BuildFileBytes(bool hasBom, IReadOnlyList<LineSpec> lines)
    {
        using MemoryStream stream = new();
        if (hasBom)
        {
            stream.Write([0xEF, 0xBB, 0xBF]);
        }

        foreach (LineSpec line in lines)
        {
            stream.Write(line.Content);
            switch (line.Terminator)
            {
                case Terminator.LineFeed:
                    stream.WriteByte((byte)'\n');
                    break;
                case Terminator.CarriageReturnLineFeed:
                    stream.Write([(byte)'\r', (byte)'\n']);
                    break;
                case Terminator.None:
                    break;
            }
        }

        return stream.ToArray();
    }

    /// Replaces one line (or appends a new one) with an over-length line, so reading the
    /// file fails exactly there. <paramref name="atEnd"/> makes it the file's final,
    /// unterminated line, which exercises LineCursor's no-newline branch since no LF can
    /// still arrive; otherwise it replaces an existing, terminated line in place and
    /// exercises the terminated branch.
    public static List<LineSpec> InjectMalformedLine(
        IReadOnlyList<LineSpec> lines, bool atEnd, int index, int malformedLength)
    {
        List<LineSpec> result = [.. lines];
        byte[] overLong = BuildOverLongContent(malformedLength);

        if (atEnd || result.Count == 0)
        {
            result.Add(new LineSpec(overLong, Terminator.None));
            return result;
        }

        int clampedIndex = Math.Min(index, result.Count - 1);
        Terminator existing = result[clampedIndex].Terminator;
        result[clampedIndex] = new LineSpec(overLong, existing == Terminator.None ? Terminator.LineFeed : existing);
        return result;
    }
}
