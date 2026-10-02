using FileSorter.LineFormat;

namespace FileSorter.Merging;

// Owns and disposes its stream.
internal sealed class RunCursor : IAsyncDisposable
{
    private readonly Stream _run;
    private readonly int _maxLineLength;

    private readonly int? _runIndex;

    // Descriptors point into _buffers[_current]; the other window may have a read in flight.
    private readonly byte[][] _buffers;
    private int _current;

    private long _bytesConsumed;
    private long _linesRead;
    private bool _streamExhausted;
    private bool _finished;

    private Task<int>? _prefetchReadTask;
    private int _prefetchCarryLength;

    private int _carryOffset;
    private int _carryLength;

    private readonly LineDescriptor[] _descriptors;
    private int _pendingCount;
    private int _pendingIndex;

    public RunCursor(Stream run, RunCursorBuffers buffers, int maxLineLength, int? runIndex = null)
    {
        // A window must carry a maximum line followed by CR and still read its LF.
        ArgumentOutOfRangeException.ThrowIfLessThan(buffers.FirstWindow.Length, maxLineLength + LineCursor.WindowSlack, nameof(buffers));
        ArgumentOutOfRangeException.ThrowIfLessThan(buffers.SecondWindow.Length, maxLineLength + LineCursor.WindowSlack, nameof(buffers));

        if (buffers.FirstWindow.Length != buffers.SecondWindow.Length)
        {
            throw new ArgumentException(
                "The two read-ahead buffers must be the same size.", nameof(buffers));
        }

        ArgumentOutOfRangeException.ThrowIfZero(buffers.Descriptors.Length, nameof(buffers));

        _run = run;
        _buffers = [buffers.FirstWindow, buffers.SecondWindow];
        _descriptors = buffers.Descriptors;
        _maxLineLength = maxLineLength;
        _runIndex = runIndex;
    }

    public LineDescriptor Current { get; private set; }
    public byte[]         Buffer => _buffers[_current];

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
            // The in-flight read must finish before its stream closes; its outcome no longer matters.
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
        (int workingIndex, int windowLength) = await AcquireWindowAsync(ct);
        if (windowLength == 0)
        {
            _finished = true;
            return false;
        }

        int count = ScanWindow(_buffers[workingIndex], windowLength, out int carryOffset, out int carryLength);
        if (count == 0)
        {
            // No carry is lost: a full unterminated window would already have failed the line-length guard.
            _finished = true;
            return false;
        }

        _current = workingIndex;
        _pendingCount = count;
        _pendingIndex = 1;
        Current = _descriptors[0];

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

    private async ValueTask<(int WorkingIndex, int WindowLength)> AcquireWindowAsync(CancellationToken ct)
    {
        if (_prefetchReadTask is null)
        {
            return (_current, await ShiftAndFillAsync(_buffers[_current], _carryOffset, _carryLength, ct));
        }

        int read = await _prefetchReadTask;
        _prefetchReadTask = null;
        int workingIndex = 1 - _current;

        int fillAmount = _buffers[workingIndex].Length - _prefetchCarryLength;
        if (read < fillAmount)
        {
            _streamExhausted = true;
        }

        _bytesConsumed += read;
        return (workingIndex, _prefetchCarryLength + read);
    }

    private int ScanWindow(byte[] buffer, int windowLength, out int carryOffset, out int carryLength)
    {
        long bufferBaseOffset = _bytesConsumed - windowLength;
        int count = 0;

        // A CR before LF is line content in runs; stripping it would change keys the spiller wrote.
        LineCursor cursor = new(
            buffer.AsSpan(0, windowLength), _maxLineLength, bufferBaseOffset, _linesRead + 1,
            stripByteOrderMark: false, stripCarriageReturn: false);
        while (count < _descriptors.Length && cursor.TryReadLine(out int offset, out int length))
        {
            _descriptors[count++] = Describe(buffer, offset, length, bufferBaseOffset);
        }

        carryOffset = cursor.CarryOffset;
        carryLength = cursor.CarryLength;

        bool stoppedOnCapacity = count == _descriptors.Length;

        if (!stoppedOnCapacity && carryLength > 0 && _streamExhausted)
        {
            int finalLength = LineCursor.UnterminatedTailLength(buffer.AsSpan(carryOffset, carryLength));
            if (finalLength > 0)
            {
                _descriptors[count++] = Describe(buffer, carryOffset, finalLength, bufferBaseOffset);
            }

            carryOffset += carryLength;
            carryLength = 0;
        }

        return count;
    }

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
        _prefetchReadTask = _run.ReadAtLeastAsync(
            other.AsMemory(carryLength, fillAmount), fillAmount, throwOnEndOfStream: false, ct).AsTask();
    }

    private async ValueTask<int> ShiftAndFillAsync(byte[] buffer, int carryOffset, int carryLength, CancellationToken ct)
    {
        if (carryLength > 0)
        {
            Array.Copy(buffer, carryOffset, buffer, 0, carryLength);
        }

        int fillAmount = buffer.Length - carryLength;
        int read = _streamExhausted
            ? 0
            : await _run.ReadAtLeastAsync(
                buffer.AsMemory(carryLength, fillAmount), fillAmount, throwOnEndOfStream: false, ct);
        if (read < fillAmount)
        {
            _streamExhausted = true;
        }

        _bytesConsumed += read;
        return carryLength + read;
    }

    private LineDescriptor Describe(byte[] buffer, int offset, int length, long bufferBaseOffset)
    {
        if (!LineDescriptor.TryCreate(buffer, offset, length, out LineDescriptor descriptor))
        {
            string previewText = MalformedLineException.PreviewOf(buffer.AsSpan(offset, length));
            long byteOffset = bufferBaseOffset + offset;

            throw _runIndex is { } index
                ? new MalformedLineException(byteOffset, _linesRead + 1, previewText, index)
                : new MalformedLineException(byteOffset, _linesRead + 1, previewText);
        }

        _linesRead++;
        return descriptor;
    }
}
