using System.Text;

namespace FileSorter.LineFormat;

internal sealed class MalformedLineException : Exception
{
    /// Used when byte-offset searches cannot determine an absolute line number.
    public const long LineNumberUnavailable = -1;

    private const int PreviewMaxBytes = 128;

    public long   ByteOffset { get; }
    public long   LineNumber { get; }
    public string Preview    { get; }   // offending bytes, truncated to a readable length

    /// <summary>
    /// Identifies the cursor within a merge. ByteOffset is relative to that cursor's stream;
    /// the executor uses this index to recover the run path and add the slice offset.
    /// </summary>
    public int? RunIndex { get; }

    public MalformedLineException(long byteOffset, long lineNumber, string preview)
        : this(MessageFor(byteOffset, lineNumber, preview, filePath: null), byteOffset, lineNumber, preview, runIndex: null)
    {
    }

    /// <param name="runIndex">Merge-local cursor index. Does not change the stream-relative byteOffset.</param>
    public MalformedLineException(long byteOffset, long lineNumber, string preview, int runIndex)
        : this(MessageFor(byteOffset, lineNumber, preview, filePath: null), byteOffset, lineNumber, preview, runIndex)
    {
    }

    /// <param name="filePath">Identifies the input, output, or run file in the diagnostic.</param>
    public MalformedLineException(long byteOffset, long lineNumber, string preview, string filePath)
        : this(MessageFor(byteOffset, lineNumber, preview, filePath), byteOffset, lineNumber, preview, runIndex: null)
    {
    }

    private MalformedLineException(string message, long byteOffset, long lineNumber, string preview, int? runIndex)
        : base(message)
    {
        ByteOffset = byteOffset;
        LineNumber = lineNumber;
        Preview = preview;
        RunIndex = runIndex;
    }

    /// Decodes the first 128 bytes of an offending line for a diagnostic.
    /// The limit keeps one hostile line from turning the error message into kilobytes of stderr.
    public static string PreviewOf(ReadOnlySpan<byte> line) =>
        Encoding.UTF8.GetString(line.Length > PreviewMaxBytes ? line[..PreviewMaxBytes] : line);

    private static string MessageFor(long byteOffset, long lineNumber, string preview, string? filePath) =>
        filePath is null
            ? $"Malformed {DescribeLine(lineNumber)} at byte offset {byteOffset}: \"{preview}\""
            : $"Malformed {DescribeLine(lineNumber)} at byte offset {byteOffset} in '{filePath}': \"{preview}\"";

    private static string DescribeLine(long lineNumber) =>
        lineNumber == LineNumberUnavailable ? "line (number unavailable)" : $"line {lineNumber}";
}
