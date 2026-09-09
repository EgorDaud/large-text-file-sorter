namespace FileSorter.Tests.Properties;

/// <summary>
/// An independent, non-streaming reading of the line-boundary rules <c>LineCursor</c>
/// implements, written against the whole file at once rather than one buffer's worth at
/// a time, and deliberately sharing no code with it. It is the oracle the randomised
/// buffer/descriptor/fill-size sweeps check the real splitter against: having no notion
/// of a buffer boundary at all, it disagrees the moment chunking or windowing changes
/// what a file splits into.
/// </summary>
internal static class NaiveLineFormat
{
    public readonly record struct NaiveLine(long Offset, int Length);

    /// <see cref="MalformedOffset"/> and <see cref="MalformedLineNumber"/> are null
    /// exactly when the file is well formed; <see cref="Lines"/> then holds every line's
    /// file offset and length, terminators excluded and a "\r\n" line's "\r" stripped.
    /// When they are set, Lines holds only what was completed before the offending line.
    public sealed record NaiveSplitResult(
        IReadOnlyList<NaiveLine> Lines, long? MalformedOffset, long? MalformedLineNumber)
    {
        public bool IsMalformed => MalformedOffset is not null;
    }

    /// <param name="stripBom">
    /// True for a user input file, where a mark at absolute offset 0 is stripped; false
    /// for a run file, where the merge side never strips one.
    /// </param>
    /// <param name="stripCarriageReturn">
    /// True for a user input file, where a '\r' immediately before '\n' is the
    /// terminator's second half; false for a run file, which is terminated by a bare
    /// '\n' throughout, so a '\r' there is always content. Only the within-file check
    /// below is gated by it: the end-of-file tail rule further down applies whatever
    /// this flag says, exactly as <c>ChunkReader</c> and <c>RunCursor</c> apply it.
    /// </param>
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

            // Exactly one immediately-preceding carriage return is stripped; anything
            // else in the line is ordinary content. Gated by stripCarriageReturn,
            // because a run file's '\r' here is content the sorter carried in.
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

        // The trailing region after the last line feed is nothing when empty, otherwise
        // the file's final, unterminated line. A lone carriage return at the very end
        // does not count against the length limit, which is why contentLength is
        // computed separately from tailLength for that check; it is also the first half
        // of a terminator whose line feed never arrived rather than content, so
        // contentLength is what the splitter keeps. A tail of nothing else
        // (contentLength 0) leaves no residual line at all.
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
