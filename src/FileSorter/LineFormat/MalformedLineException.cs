namespace FileSorter.LineFormat;

internal sealed class MalformedLineException : Exception
{
    /// Used when byte-offset searches cannot determine an absolute line number.
    public const long LineNumberUnavailable = -1;

    public long   ByteOffset { get; }
    public long   LineNumber { get; }
    public string Preview    { get; }   // offending bytes, truncated to a readable length

    /// <summary>
    /// Identifies the cursor within a merge. ByteOffset is relative to that cursor's stream;
    /// the executor uses this index to recover the run path and add the slice offset.
    /// </summary>
    public int? RunIndex { get; }

    public MalformedLineException(long byteOffset, long lineNumber, string preview)
        : base($"Malformed {DescribeLine(lineNumber)} at byte offset {byteOffset}: \"{preview}\"")
    {
        ByteOffset = byteOffset;
        LineNumber = lineNumber;
        Preview = preview;
    }

    /// <param name="runIndex">Merge-local cursor index. Does not change the stream-relative byteOffset.</param>
    public MalformedLineException(long byteOffset, long lineNumber, string preview, int runIndex)
        : base($"Malformed {DescribeLine(lineNumber)} at byte offset {byteOffset}: \"{preview}\"")
    {
        ByteOffset = byteOffset;
        LineNumber = lineNumber;
        Preview = preview;
        RunIndex = runIndex;
    }

    /// <param name="filePath">Identifies the input, output, or run file in the diagnostic.</param>
    public MalformedLineException(long byteOffset, long lineNumber, string preview, string filePath)
        : base($"Malformed {DescribeLine(lineNumber)} at byte offset {byteOffset} in '{filePath}': \"{preview}\"")
    {
        ByteOffset = byteOffset;
        LineNumber = lineNumber;
        Preview = preview;
    }

    private static string DescribeLine(long lineNumber) =>
        lineNumber == LineNumberUnavailable ? "line (number unavailable)" : $"line {lineNumber}";
}
