using CsCheck;

namespace FileSorter.Tests.Properties;

internal static class RandomLineFileGen
{
    public const int MaxLineLength = 48;

    // Large enough that most files span several of the sweeps' ~2 KiB buffer fills.
    public const int SweepMaxLineCount = 3000;

    public enum Terminator { LineFeed, CarriageReturnLineFeed, None }

    public sealed record LineSpec(byte[] Content, Terminator Terminator);

    // '\r' is deliberately allowed in content; '\n' would split the line.
    private static readonly Gen<byte> ContentByte =
        Gen.Byte[0, 255].Where(b => b is not (byte)'\n');

    // One roll in ten pins the length at exactly MaxLineLength, the boundary under test.
    private static readonly Gen<int> WellFormedLength =
        Gen.Int[0, 9].SelectMany(roll => roll == 0 ? Gen.Const(MaxLineLength) : Gen.Int[2, MaxLineLength - 1]);

    public static readonly Gen<byte[]> WellFormedContent =
        WellFormedLength.SelectMany(length => ContentByte.Array[length - 2].Select(BuildContent));

    public static readonly Gen<Terminator> NonFinalTerminator =
        Gen.Bool.Select(crlf => crlf ? Terminator.CarriageReturnLineFeed : Terminator.LineFeed);

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
