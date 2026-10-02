namespace FileSorter.LineFormat;

internal ref struct LineCursor
{
    private const byte LineFeed = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';

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

    // The content length of an unterminated tail at EOF. A final bare CR is a terminator,
    // not line content, so a tail of only CR has no content. ChunkReader, RunCursor and
    // OutputVerifier all finish a stream with this rule; keeping it here stops them drifting.
    public static int UnterminatedTailLength(ReadOnlySpan<byte> tail) =>
        tail.Length > 0 && tail[^1] == CarriageReturn ? tail.Length - 1 : tail.Length;

    public bool TryReadLine(out int offset, out int length)
    {
        ReadOnlySpan<byte> remaining = _block[_position..];
        int newline = remaining.IndexOf(LineFeed);

        if (newline < 0)
        {
            // Allow one trailing CR beyond the content limit while waiting for LF.
            // The completed-line check resolves whether it is content; callers handle EOF.
            // This carry allowance applies even when stripCarriageReturn is false.
            if (UnterminatedTailLength(remaining) > _maxLineLength)
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
        return new MalformedLineException(
            _blockBaseOffset + _position, _firstLineNumber + _linesRead, MalformedLineException.PreviewOf(offending));
    }
}
