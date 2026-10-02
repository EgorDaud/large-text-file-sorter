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
        if (_prefetch is null)
        {
            PooledBuffer first = await _pool.AcquireAsync(ct);
            try
            {
                int firstRead = await FillAsync(first.Bytes, _reserve, first.Bytes.Length - _reserve, ct);
                _prefetch = new Prefetch(first, CarryLength: 0, SpanStart: _reserve, Task.FromResult(firstRead), CountIsCapped: false);
            }
            catch
            {
                first.Dispose();
                throw;
            }
        }

        Prefetch current = _prefetch.Value;
        PooledBuffer slot = current.Slot;
        _prefetch = null;

        try
        {
            int freshCount = await current.FillTask;
            BytesConsumed += freshCount;
            int totalBytes = current.CarryLength + freshCount;

            bool thisExhausted = !current.CountIsCapped && freshCount < _fillAmount;

            if (totalBytes == 0)
            {
                slot.Dispose();
                return null;
            }

            PooledBuffer next = await _pool.AcquireAsync(ct);
            Task<int> nextFillTask;
            try
            {
                nextFillTask = FillAsync(next.Bytes, _reserve, next.Bytes.Length - _reserve, ct);
            }
            catch
            {
                next.Dispose();
                throw;
            }

            int count;
            int carryOffset;
            int carryOverLength;
            try
            {
                count = ParseLines(
                    slot, current.SpanStart, totalBytes, BytesConsumed - totalBytes, thisExhausted,
                    out carryOffset, out carryOverLength);
                LinesRead += count;
            }
            catch
            {
                await ReleaseUnusedPrefetchDuringUnwindAsync(next, nextFillTask);
                throw;
            }

            if (!thisExhausted || carryOverLength > 0)
            {
                await FoldCarryIntoNextAsync(next, nextFillTask, slot.Bytes, carryOffset, carryOverLength);
            }
            else
            {
                await ReleaseUnusedPrefetchAsync(next, nextFillTask);
            }

            return new Chunk(slot, count);
        }
        catch
        {
            slot.Dispose();
            throw;
        }
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

    private async ValueTask FoldCarryIntoNextAsync(
        PooledBuffer next, Task<int> nextFillTask, byte[] previousBuffer, int carryOffset, int carryLength)
    {
        try
        {
            if (carryLength <= _reserve)
            {
                int spanStart = _reserve - carryLength;
                if (carryLength > 0)
                {
                    Array.Copy(previousBuffer, carryOffset, next.Bytes, spanStart, carryLength);
                }

                _prefetch = new Prefetch(next, carryLength, spanStart, nextFillTask, CountIsCapped: false);
                return;
            }

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

            if (carryLength > 0)
            {
                Array.Copy(previousBuffer, carryOffset, next.Bytes, 0, carryLength);
            }

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

    private async Task<int> FillAsync(byte[] buffer, int destinationOffset, int destinationLength, CancellationToken ct)
    {
        int fromPending = Math.Min(_pendingLength, destinationLength);
        if (fromPending > 0)
        {
            Array.Copy(_pending, 0, buffer, destinationOffset, fromPending);

            int remainingPending = _pendingLength - fromPending;
            if (remainingPending > 0)
            {
                Array.Copy(_pending, fromPending, _pending, 0, remainingPending);
            }

            _pendingLength = remainingPending;
        }

        int fromInput = await _input.ReadAtLeastAsync(
            buffer.AsMemory(destinationOffset + fromPending, destinationLength - fromPending),
            destinationLength - fromPending, throwOnEndOfStream: false, ct);
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
