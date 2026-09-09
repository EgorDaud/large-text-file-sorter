using System.Text;
using FileSorter.LineFormat;

namespace FileSorter.Merging;

// A double-buffered line cursor over one run. It keeps at most one read per run in flight,
// issuing the next-window read while callers consume the current window. The cursor owns
// and disposes its stream.
internal sealed class RunCursor : IAsyncDisposable
{
    // Long enough to identify a bad line without flooding the console.
    private const int PreviewMaxBytes = 128;

    private const byte CarriageReturn = (byte)'\r';

    private readonly Stream _run;
    private readonly int _maxLineLength;

    // Index in the caller's run list for remapping slice-relative malformed-line offsets.
    private readonly int? _runIndex;

    // Current descriptors reference _current. The other equal-size window may prefetch.
    private readonly byte[][] _buffers;
    private int _current;

    private long _bytesConsumed;
    private long _linesRead;
    private bool _streamExhausted;
    private bool _finished;

    // Read into the other buffer, issued while the current window is delivered.
    private Task<int>? _prefetchReadTask;
    private int _prefetchCarryLength;

    // Carry retained in the current buffer after EOF when descriptors fill before bytes do.
    private int _carryOffset;
    private int _carryLength;

    // Caller-supplied descriptors are reused for every window and included in the memory budget.
    private readonly LineDescriptor[] _descriptors;
    private int _pendingCount;
    private int _pendingIndex;

    public RunCursor(Stream run, RunCursorBuffers buffers, int maxLineLength, int? runIndex = null)
    {
        // A window must exceed maxLineLength + 1. A maximum line followed by CR can be
        // carried intact, leaving one byte needed to read its terminating LF.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(buffers.FirstWindow.Length, maxLineLength + 1, nameof(buffers));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(buffers.SecondWindow.Length, maxLineLength + 1, nameof(buffers));

        // The memory plan prices two equal read-ahead windows.
        if (buffers.FirstWindow.Length != buffers.SecondWindow.Length)
        {
            throw new ArgumentException(
                "The two read-ahead buffers must be the same size.", nameof(buffers));
        }

        // No descriptors would prevent progress.
        ArgumentOutOfRangeException.ThrowIfZero(buffers.Descriptors.Length, nameof(buffers));

        _run = run;
        _buffers = [buffers.FirstWindow, buffers.SecondWindow];
        _descriptors = buffers.Descriptors;
        _maxLineLength = maxLineLength;
        _runIndex = runIndex;
    }

    public LineDescriptor Current { get; private set; }
    public byte[]         Buffer => _buffers[_current];

    // Fast path for already-scanned descriptors. Only MoveNextAsync advances a window.
    public bool TryMoveNext()
    {
        if (_pendingIndex < _pendingCount)
        {
            Current = _descriptors[_pendingIndex++];
            return true;
        }

        return false;
    }

    public async ValueTask<bool> MoveNextAsync(CancellationToken ct)
    {
        if (TryMoveNext())
        {
            return true;
        }

        if (_finished)
        {
            return false;
        }

        return await AdvanceAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_prefetchReadTask is not null)
        {
            // Await the overlapped read before closing its stream. Cleanup ignores its
            // result because the caller is already disposing this cursor.
            try
            {
                await _prefetchReadTask;
            }
            catch
            {
            }
        }

        await _run.DisposeAsync();
    }

    private async ValueTask<bool> AdvanceAsync(CancellationToken ct)
    {
        int workingIndex;
        int windowLength;

        if (_prefetchReadTask is not null)
        {
            // Await the other window's read, issued while this window was delivered.
            int read = await _prefetchReadTask;
            workingIndex = 1 - _current;

            // Equal window sizes make this the amount requested by the prefetch.
            int fillAmount = _buffers[workingIndex].Length - _prefetchCarryLength;
            if (read < fillAmount)
            {
                _streamExhausted = true;
            }

            _bytesConsumed += read;
            windowLength = _prefetchCarryLength + read;
            _prefetchReadTask = null;
        }
        else
        {
            // The first window or buffered carry after EOF has no read to await.
            workingIndex = _current;
            windowLength = await ShiftAndFillAsync(_buffers[workingIndex], _carryOffset, _carryLength, ct);
        }

        if (windowLength == 0)
        {
            _finished = true;
            return false;
        }

        byte[] buffer = _buffers[workingIndex];
        long bufferBaseOffset = _bytesConsumed - windowLength;
        int count = 0;
        // Runs have no BOM and use LF terminators. Preserve a preceding CR as content so
        // the key bytes match those written by the spiller.
        LineCursor cursor = new(
            buffer.AsSpan(0, windowLength), _maxLineLength, bufferBaseOffset, _linesRead + 1,
            stripByteOrderMark: false, stripCarriageReturn: false);
        while (count < _descriptors.Length && cursor.TryReadLine(out int offset, out int length))
        {
            _descriptors[count++] = Describe(buffer, offset, length, bufferBaseOffset);
        }

        int carryOffset = cursor.CarryOffset;
        int carryLength = cursor.CarryLength;

        // Descriptor capacity can end a window before its bytes are exhausted.
        bool stoppedOnCapacity = count == _descriptors.Length;

        // At EOF, finalize an unterminated tail that LineCursor cannot distinguish from a
        // partial fill. A trailing CR is treated as an incomplete terminator.
        if (!stoppedOnCapacity && carryLength > 0 && _streamExhausted)
        {
            bool tailEndsInCarriageReturn = buffer[carryOffset + carryLength - 1] == CarriageReturn;
            int finalLength = tailEndsInCarriageReturn ? carryLength - 1 : carryLength;
            if (finalLength > 0)
            {
                _descriptors[count++] = Describe(buffer, carryOffset, finalLength, bufferBaseOffset);
            }

            carryOffset += carryLength;
            carryLength = 0;
        }

        if (count == 0)
        {
            // At EOF no unreported carry remains. A full unterminated window would already
            // have failed LineCursor's line-length guard.
            _finished = true;
            return false;
        }

        _current = workingIndex;
        _pendingCount = count;
        _pendingIndex = 1;
        Current = _descriptors[0];

        // Prefetch at scan time to overlap delivery. At EOF retain carry instead.
        if (_streamExhausted)
        {
            _carryOffset = carryOffset;
            _carryLength = carryLength;
        }
        else
        {
            IssuePrefetch(workingIndex, carryOffset, carryLength, ct);
        }

        return true;
    }

    // Copies carry to the other buffer and begins its read. The current buffer is still
    // safe to read here, before descriptors expose it through Buffer.
    private void IssuePrefetch(int currentIndex, int carryOffset, int carryLength, CancellationToken ct)
    {
        byte[] current = _buffers[currentIndex];
        byte[] other = _buffers[1 - currentIndex];

        if (carryLength > 0)
        {
            Array.Copy(current, carryOffset, other, 0, carryLength);
        }

        int fillAmount = other.Length - carryLength;
        _prefetchCarryLength = carryLength;
        _prefetchReadTask = FillAsync(other.AsMemory(carryLength, fillAmount), ct).AsTask();
    }

    // Shifts carry and fills synchronously for the first window or buffered carry after EOF.
    private async ValueTask<int> ShiftAndFillAsync(byte[] buffer, int carryOffset, int carryLength, CancellationToken ct)
    {
        if (carryLength > 0)
        {
            Array.Copy(buffer, carryOffset, buffer, 0, carryLength);
        }

        int fillAmount = buffer.Length - carryLength;
        int read = _streamExhausted ? 0 : await FillAsync(buffer.AsMemory(carryLength, fillAmount), ct);
        if (read < fillAmount)
        {
            _streamExhausted = true;
        }

        _bytesConsumed += read;
        return carryLength + read;
    }

    private LineDescriptor Describe(byte[] buffer, int offset, int length, long bufferBaseOffset)
    {
        ReadOnlySpan<byte> line = buffer.AsSpan(offset, length);
        if (!LineParser.TryParse(line, out long number, out int stringStart))
        {
            ReadOnlySpan<byte> preview = line.Length > PreviewMaxBytes ? line[..PreviewMaxBytes] : line;
            string previewText = Encoding.UTF8.GetString(preview);
            long byteOffset = bufferBaseOffset + offset;

            // The caller uses RunIndex to translate a slice-relative offset to its run path.
            throw _runIndex is { } index
                ? new MalformedLineException(byteOffset, _linesRead + 1, previewText, index)
                : new MalformedLineException(byteOffset, _linesRead + 1, previewText);
        }

        _linesRead++;
        int stringOffset = offset + stringStart;
        int stringLength = LineDescriptor.StringLengthOf(offset, length, stringOffset);
        ulong prefix = LineDescriptor.BuildPrefix(buffer.AsSpan(stringOffset, stringLength));
        return new LineDescriptor(prefix, number, offset, length, stringOffset);
    }

    private async ValueTask<int> FillAsync(Memory<byte> destination, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = await _run.ReadAsync(destination[totalRead..], ct);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
}
