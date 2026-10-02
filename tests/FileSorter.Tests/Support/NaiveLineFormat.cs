namespace FileSorter.Tests.Support;

// Whole-file oracle for LineCursor's splitting rules; deliberately shares no code with it.
internal static class NaiveLineFormat
{
    public readonly record struct NaiveLine(long Offset, int Length);

    public sealed record NaiveSplitResult(
        IReadOnlyList<NaiveLine> Lines, long? MalformedOffset, long? MalformedLineNumber)
    {
        public bool IsMalformed => MalformedOffset is not null;
    }

    public static NaiveSplitResult Split(byte[] input, int maxLineLength, bool stripBom, bool stripCarriageReturn)
    {
        int pos = stripBom && StartsWithByteOrderMark(input) ? 3 : 0;
        List<NaiveLine> lines = [];
        long lineNumber = 0;

        while (true)
        {
            int lineFeed = Array.IndexOf(input, (byte)'\n', pos);
            if (lineFeed < 0)
            {
                break;
            }

            int lineStart = pos;
            int contentEnd = lineFeed;

            if (stripCarriageReturn && contentEnd > lineStart && input[contentEnd - 1] == (byte)'\r')
            {
                contentEnd--;
            }

            int length = contentEnd - lineStart;
            lineNumber++;
            if (length > maxLineLength)
            {
                return new NaiveSplitResult(lines, lineStart, lineNumber);
            }

            lines.Add(new NaiveLine(lineStart, length));
            pos = lineFeed + 1;
        }

        // A final '\r' with no '\n' is a cut-off terminator, stripped even when stripCarriageReturn is false, as LineCursor does.
        if (pos < input.Length)
        {
            int tailStart = pos;
            int tailLength = input.Length - pos;
            bool endsInCarriageReturn = input[^1] == (byte)'\r';
            int contentLength = endsInCarriageReturn ? tailLength - 1 : tailLength;
            lineNumber++;
            if (contentLength > maxLineLength)
            {
                return new NaiveSplitResult(lines, tailStart, lineNumber);
            }

            if (contentLength > 0)
            {
                lines.Add(new NaiveLine(tailStart, contentLength));
            }
        }

        return new NaiveSplitResult(lines, MalformedOffset: null, MalformedLineNumber: null);
    }

    private static bool StartsWithByteOrderMark(byte[] input) =>
        input.Length >= 3 && input[0] == 0xEF && input[1] == 0xBB && input[2] == 0xBF;
}
