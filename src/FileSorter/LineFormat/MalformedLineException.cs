using System.Text;

namespace FileSorter.LineFormat;

internal sealed class MalformedLineException : Exception
{
    public const long LineNumberUnavailable = -1;

    private const int PreviewMaxBytes = 128;

    public long   ByteOffset { get; }
    public long   LineNumber { get; }
    public string Preview    { get; }

    // When set, ByteOffset is relative to that merge cursor's stream, not to a file.
    public int? RunIndex { get; }

    public MalformedLineException(long byteOffset, long lineNumber, string preview)
        : this(MessageFor(byteOffset, lineNumber, preview, filePath: null), byteOffset, lineNumber, preview, runIndex: null)
    {
    }

    public MalformedLineException(long byteOffset, long lineNumber, string preview, int runIndex)
        : this(MessageFor(byteOffset, lineNumber, preview, filePath: null), byteOffset, lineNumber, preview, runIndex)
    {
    }

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

    public static string PreviewOf(ReadOnlySpan<byte> line) =>
        Encoding.UTF8.GetString(line.Length > PreviewMaxBytes ? line[..PreviewMaxBytes] : line);

    private static string MessageFor(long byteOffset, long lineNumber, string preview, string? filePath) =>
        filePath is null
            ? $"Malformed {DescribeLine(lineNumber)} at byte offset {byteOffset}: \"{preview}\""
            : $"Malformed {DescribeLine(lineNumber)} at byte offset {byteOffset} in '{filePath}': \"{preview}\"";

    private static string DescribeLine(long lineNumber) =>
        lineNumber == LineNumberUnavailable ? "line (number unavailable)" : $"line {lineNumber}";
}
