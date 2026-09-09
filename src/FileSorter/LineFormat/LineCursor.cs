using System.Text;

namespace FileSorter.LineFormat;

internal ref struct LineCursor
{
    private const byte LineFeed = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';

    private const int PreviewMaxBytes = 128;

    private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private readonly ReadOnlySpan<byte> _block;
    private readonly int _maxLineLength;
    private readonly long _blockBaseOffset;
    private readonly long _firstLineNumber;
    private readonly bool _stripCarriageReturn;
    private int _position;
    private long _linesRead;

    /// <param name="blockBaseOffset">
    /// File offset of the block, used for diagnostics and BOM detection at offset zero.
    /// </param>
    /// <param name="firstLineNumber">File line number of the first line in the block.</param>
    /// <param name="stripByteOrderMark">
    /// Strip a UTF-8 BOM only at file offset zero. Disable for sorter-produced files.
    /// </param>
    /// <param name="stripCarriageReturn">
    /// Strip one CR before LF in user input. Disable for runs and output, where CR is content
    /// and LF alone terminates each line.
    /// </param>
    public LineCursor(
        ReadOnlySpan<byte> block, int maxLineLength, long blockBaseOffset = 0, long firstLineNumber = 1,
        bool stripByteOrderMark = true, bool stripCarriageReturn = true)
    {
        _block = block;
        _maxLineLength = maxLineLength;
        _blockBaseOffset = blockBaseOffset;
        _firstLineNumber = firstLineNumber;
        _linesRead = 0;
        _stripCarriageReturn = stripCarriageReturn;
        _position = stripByteOrderMark && blockBaseOffset == 0 && block.StartsWith(ByteOrderMark)
            ? ByteOrderMark.Length
            : 0;
    }

    public int CarryOffset => _position;
    public int CarryLength => _block.Length - _position;

    public bool TryReadLine(out int offset, out int length)
    {
        ReadOnlySpan<byte> remaining = _block[_position..];
        int newline = remaining.IndexOf(LineFeed);

        if (newline < 0)
        {
            // Allow one trailing CR beyond the content limit while waiting for LF.
            // The completed-line check resolves whether it is content; callers handle EOF.
            // This carry allowance applies even when stripCarriageReturn is false.
            bool endsInCarriageReturn = remaining.Length > 0 && remaining[^1] == CarriageReturn;
            int contentLength = endsInCarriageReturn ? remaining.Length - 1 : remaining.Length;
            if (contentLength > _maxLineLength)
            {
                throw BuildException(remaining);
            }

            offset = 0;
            length = 0;
            return false;
        }

        int lineEnd = _position + newline;
        bool hasCarriageReturn = _stripCarriageReturn && lineEnd > _position && _block[lineEnd - 1] == CarriageReturn;
        int contentEnd = hasCarriageReturn ? lineEnd - 1 : lineEnd;

        offset = _position;
        length = contentEnd - _position;

        if (length > _maxLineLength)
        {
            throw BuildException(_block[_position..contentEnd]);
        }

        _position = lineEnd + 1;
        _linesRead++;
        return true;
    }

    private readonly MalformedLineException BuildException(ReadOnlySpan<byte> offending)
    {
        ReadOnlySpan<byte> preview = offending.Length > PreviewMaxBytes ? offending[..PreviewMaxBytes] : offending;

        return new MalformedLineException(
            _blockBaseOffset + _position, _firstLineNumber + _linesRead, Encoding.UTF8.GetString(preview));
    }
}
