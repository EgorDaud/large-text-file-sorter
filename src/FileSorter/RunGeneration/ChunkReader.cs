using System.Diagnostics.CodeAnalysis;
using FileSorter.LineFormat;

namespace FileSorter.RunGeneration;

// A prefetch fills [Reserve, BufferSize) before the previous chunk's carry is known; the carry lands just below Reserve.
internal sealed class ChunkReader : IAsyncDisposable
{
    [SuppressMessage(
        "Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The caller owns this stream. RunGenerationDriver declares it before the reader " +
        "precisely so its `using` outlives this instance, and disposing it here would close a stream the " +
        "driver still holds.")]
    private readonly Stream _input;
    private readonly BufferPool _pool;
    private readonly int _maxLineLength;

    private readonly int _reserve;

    // Carry larger than Reserve (descriptor exhaustion) displaces prefetched bytes; the stream cannot reread them.
    private readonly byte[] _pending;
    private int _pendingLength;

    private readonly int _fillAmount;

    private Prefetch? _prefetch;

    public ChunkReader(Stream input, BufferPool pool, int maxLineLength)
    {
        _reserve = maxLineLength + LineCursor.WindowSlack;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pool.BufferSize, _reserve, nameof(pool));

        ArgumentOutOfRangeException.ThrowIfLessThan(pool.DescriptorCapacity, 1, nameof(pool));

        _input = input;
        _pool = pool;
        _maxLineLength = maxLineLength;
        _pending = new byte[pool.BufferSize];
        _fillAmount = pool.BufferSize - _reserve;
    }

    public long BytesConsumed { get; private set; }
    public long LinesRead     { get; private set; }

    public async ValueTask<Chunk?> ReadNextAsync(CancellationToken ct)
    {
        Prefetch current = _prefetch ?? await StartFirstPrefetchAsync(ct);
        _prefetch = null;

        // From here this call owns the slot: it goes to the caller inside the Chunk, or is released on any other exit.
        PooledBuffer slot = current.Slot;

        try
        {
            int freshCount = await current.FillTask;
            BytesConsumed += freshCount;
            int totalBytes = current.CarryLength + freshCount;

            if (totalBytes == 0)
            {
                slot.Dispose();
                return null;
            }

            PooledBuffer next = await _pool.AcquireAsync(ct);
            Task<int> nextFillTask = FillAsync(next.Bytes, ct);

            bool thisExhausted = !current.CountIsCapped && freshCount < _fillAmount;

            int count;
            int carryOffset;
            int carryLength;
            try
            {
                count = ParseLines(
                    slot, current.SpanStart, totalBytes, BytesConsumed - totalBytes, thisExhausted,
                    out carryOffset, out carryLength);
                LinesRead += count;
            }
            catch
            {
                await ReleaseUnusedPrefetchDuringUnwindAsync(next, nextFillTask);
                throw;
            }

            await HandOverNextSlotAsync(next, nextFillTask, slot.Bytes, thisExhausted, carryOffset, carryLength);

            return new Chunk(slot, count);
        }
        catch
        {
            slot.Dispose();
            throw;
        }
    }

    // A failed or cancelled fill surfaces when ReadNextAsync awaits it, and that call releases the slot.
    private async ValueTask<Prefetch> StartFirstPrefetchAsync(CancellationToken ct)
    {
        PooledBuffer first = await _pool.AcquireAsync(ct);
        return new Prefetch(first, CarryLength: 0, SpanStart: _reserve, FillAsync(first.Bytes, ct), CountIsCapped: false);
    }

    private int ParseLines(
        PooledBuffer slot, int spanStart, int totalBytes, long bufferBaseOffset, bool exhausted,
        out int carryOffset, out int carryLength)
    {
        int count = 0;
        LineCursor cursor = new(slot.Bytes.AsSpan(spanStart, totalBytes), _maxLineLength, bufferBaseOffset, LinesRead + 1);

        // The cursor's offsets are span-relative; descriptors and Describe are buffer-relative.
        long fileOffsetOfBufferZero = bufferBaseOffset - spanStart;

        while (count < slot.Lines.Length && cursor.TryReadLine(out int offset, out int length))
        {
            slot.Lines[count] = Describe(slot.Bytes, spanStart + offset, length, fileOffsetOfBufferZero, LinesRead + count + 1);
            count++;
        }

        carryOffset = spanStart + cursor.CarryOffset;
        carryLength = cursor.CarryLength;

        // Descriptor exhaustion leaves carry-over, even when it contains complete lines.
        bool stoppedOnCapacity = count == slot.Lines.Length;

        // A final bare CR is a terminator, not line content.
        if (!stoppedOnCapacity && carryLength > 0 && exhausted)
        {
            int finalLength = LineCursor.UnterminatedTailLength(slot.Bytes.AsSpan(carryOffset, carryLength));
            if (finalLength > 0)
            {
                slot.Lines[count] = Describe(slot.Bytes, carryOffset, finalLength, fileOffsetOfBufferZero, LinesRead + count + 1);
                count++;
            }

            carryOffset += carryLength;
            carryLength = 0;
        }

        return count;
    }

    // Takes ownership of next: it becomes the prefetch with the carry in front of its fresh bytes, or it is released.
    private async ValueTask HandOverNextSlotAsync(
        PooledBuffer next, Task<int> nextFillTask, byte[] previousBuffer, bool thisExhausted, int carryOffset, int carryLength)
    {
        if (thisExhausted && carryLength == 0)
        {
            await ReleaseUnusedPrefetchAsync(next, nextFillTask);
        }
        else if (carryLength <= _reserve)
        {
            int spanStart = _reserve - carryLength;
            if (carryLength > 0)
            {
                Array.Copy(previousBuffer, carryOffset, next.Bytes, spanStart, carryLength);
            }

            _prefetch = new Prefetch(next, carryLength, spanStart, nextFillTask, CountIsCapped: false);
        }
        else
        {
            await RebuildAroundOversizedCarryAsync(next, nextFillTask, previousBuffer, carryOffset, carryLength);
        }
    }

    // The only place the overlap is lost: the carry goes before bytes that are already in next, so its fill must be over.
    private async ValueTask RebuildAroundOversizedCarryAsync(
        PooledBuffer next, Task<int> nextFillTask, byte[] previousBuffer, int carryOffset, int carryLength)
    {
        try
        {
            int read = await nextFillTask;

            int keep = Math.Min(read, next.Bytes.Length - carryLength);
            int overflow = read - keep;
            if (overflow > 0)
            {
                // Overflow is at most one fill, so _pending is large enough.
                Array.Copy(next.Bytes, _reserve + keep, _pending, 0, overflow);
                _pendingLength = overflow;
            }

            if (keep > 0)
            {
                // This move overwrites the overflow bytes, so they must be saved first.
                Array.Copy(next.Bytes, _reserve, next.Bytes, carryLength, keep);
            }

            Array.Copy(previousBuffer, carryOffset, next.Bytes, 0, carryLength);

            _prefetch = new Prefetch(next, carryLength, SpanStart: 0, Task.FromResult(keep), CountIsCapped: overflow > 0);
        }
        catch
        {
            next.Dispose();
            throw;
        }
    }

    // Must rethrow I/O failures, or a failed last read would let the sort appear successful.
    private static async ValueTask ReleaseUnusedPrefetchAsync(PooledBuffer slot, Task<int> fillTask)
    {
        try
        {
            await fillTask;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            slot.Dispose();
        }
    }

    // A primary exception is already propagating, and one thrown from here would replace it.
    private static async ValueTask ReleaseUnusedPrefetchDuringUnwindAsync(PooledBuffer slot, Task<int> fillTask)
    {
        try
        {
            await fillTask;
        }
        catch
        {
        }
        finally
        {
            slot.Dispose();
        }
    }

    private static LineDescriptor Describe(byte[] buffer, int offset, int length, long fileOffsetOfBufferZero, long lineNumber)
    {
        if (!LineDescriptor.TryCreate(buffer, offset, length, out LineDescriptor descriptor))
        {
            throw new MalformedLineException(
                fileOffsetOfBufferZero + offset, lineNumber, MalformedLineException.PreviewOf(buffer.AsSpan(offset, length)));
        }

        return descriptor;
    }

    private async Task<int> FillAsync(byte[] buffer, CancellationToken ct)
    {
        int fromPending = Math.Min(_pendingLength, _fillAmount);
        if (fromPending > 0)
        {
            Array.Copy(_pending, 0, buffer, _reserve, fromPending);

            int remainingPending = _pendingLength - fromPending;
            if (remainingPending > 0)
            {
                Array.Copy(_pending, fromPending, _pending, 0, remainingPending);
            }

            _pendingLength = remainingPending;
        }

        int fromInput = await _input.ReadAtLeastAsync(
            buffer.AsMemory(_reserve + fromPending, _fillAmount - fromPending),
            _fillAmount - fromPending, throwOnEndOfStream: false, ct);
        return fromPending + fromInput;
    }

    public async ValueTask DisposeAsync()
    {
        if (_prefetch is not { } prefetch)
        {
            return;
        }

        _prefetch = null;

        // The fill may still be writing into the slot and reading the stream.
        try
        {
            await prefetch.FillTask;
        }
        catch
        {
        }

        prefetch.Slot.Dispose();
    }

    // CountIsCapped: FillTask reports retained bytes, not an I/O count, so a short value is not EOF; the rest is in _pending.
    private readonly record struct Prefetch(
        PooledBuffer Slot, int CarryLength, int SpanStart, Task<int> FillTask, bool CountIsCapped);
}
