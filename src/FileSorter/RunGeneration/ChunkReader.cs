using System.Diagnostics.CodeAnalysis;
using System.Text;
using FileSorter.LineFormat;

namespace FileSorter.RunGeneration;

// Holds a delivered slot and a prefetched slot. Every prefetch fills [Reserve, BufferSize)
// before parsing reveals the preceding chunk's carry, which overlaps I/O and parsing.
internal sealed class ChunkReader : IAsyncDisposable
{
    // Keep malformed-line diagnostics useful without printing a full line.
    private const int PreviewMaxBytes = 128;

    private const byte CarriageReturn = (byte)'\r';

    [SuppressMessage(
        "Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The caller owns this stream. RunGenerationDriver declares it before the reader " +
        "precisely so its `using` outlives this instance, and disposing it here would close a stream the " +
        "driver still holds.")]
    private readonly Stream _input;
    private readonly BufferPool _pool;
    private readonly int _maxLineLength;

    // Reserve holds a partial line and a possible trailing CR (maxLineLength + 1 bytes),
    // plus one byte so a prefetched slot always has room for fresh data.
    private readonly int _reserve;

    // Descriptor exhaustion can leave more than one partial line. Preserve displaced
    // prefetched bytes here because the input stream cannot reread them.
    private readonly byte[] _pending;
    private int _pendingLength;

    // Each ordinary fill has this fixed size. Exhaustion is decided from the matching
    // fill, because a look-ahead fill can finish first. Rebuilt prefetches are excluded.
    private readonly int _fillAmount;

    // Prefetched slot state: folded carry, valid-span start, and fresh-byte count.
    private PooledBuffer? _prefetchSlot;
    private int _prefetchCarryLength;
    private int _prefetchSpanStart;
    private Task<int>? _prefetchFillTask;

    // A rebuilt prefetch reports retained bytes, not an I/O count; a short value then
    // does not mean end of stream because remaining bytes are in _pending.
    private bool _prefetchCountIsCapped;

    public ChunkReader(Stream input, BufferPool pool, int maxLineLength)
    {
        _reserve = maxLineLength + 2;

        // The fixed fresh-fill region must leave at least one byte.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pool.BufferSize, _reserve, nameof(pool));

        // A zero-capacity descriptor array would produce empty chunks forever.
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
        if (_prefetchSlot is null)
        {
            // The first fill has no predecessor to overlap, but uses the same region.
            PooledBuffer first = await _pool.AcquireAsync(ct);
            try
            {
                int firstRead = await FillAsync(first.Bytes, _reserve, first.Bytes.Length - _reserve, ct);
                _prefetchSlot = first;
                _prefetchCarryLength = 0;
                _prefetchSpanStart = _reserve;
                _prefetchFillTask = Task.FromResult(firstRead);
                _prefetchCountIsCapped = false;
            }
            catch
            {
                first.Dispose();
                throw;
            }
        }

        PooledBuffer slot = _prefetchSlot.Value;
        int carryLength = _prefetchCarryLength;
        int spanStart = _prefetchSpanStart;
        Task<int> fillTask = _prefetchFillTask!;
        bool countIsCapped = _prefetchCountIsCapped;
        _prefetchSlot = null;
        _prefetchFillTask = null;

        try
        {
            int freshCount = await fillTask;
            BytesConsumed += freshCount;
            int totalBytes = carryLength + freshCount;

            // Use this chunk's fill only. A rebuilt prefetch can be short because carry
            // consumed its space, while remaining stream data is still pending.
            bool thisExhausted = !countIsCapped && freshCount < _fillAmount;

            if (totalBytes == 0)
            {
                slot.Dispose();
                return null;
            }

            // Start the next fill before parsing to overlap the work. The final chunk
            // releases this speculative slot.
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
                long bufferBaseOffset = BytesConsumed - totalBytes;
                count = 0;
                LineCursor cursor = new(slot.Bytes.AsSpan(spanStart, totalBytes), _maxLineLength, bufferBaseOffset, LinesRead + 1);

                // Describe uses buffer-relative offsets, while the cursor uses span-relative ones.
                long fileOffsetOfBufferZero = bufferBaseOffset - spanStart;

                // Descriptors carry slot-relative offsets for sorting and spilling.
                while (count < slot.Lines.Length && cursor.TryReadLine(out int offset, out int length))
                {
                    slot.Lines[count] = Describe(slot.Bytes, spanStart + offset, length, fileOffsetOfBufferZero, LinesRead + count + 1);
                    count++;
                }

                carryOffset = spanStart + cursor.CarryOffset;
                carryOverLength = cursor.CarryLength;

                // Descriptor exhaustion leaves carry-over, even when it contains complete lines.
                bool stoppedOnCapacity = count == slot.Lines.Length;

                // Only this reader knows the stream ended, so it emits an unterminated tail.
                // A final bare CR is a terminator, not line content.
                if (!stoppedOnCapacity && carryOverLength > 0 && thisExhausted)
                {
                    bool tailEndsInCarriageReturn = slot.Bytes[carryOffset + carryOverLength - 1] == CarriageReturn;
                    int finalLength = tailEndsInCarriageReturn ? carryOverLength - 1 : carryOverLength;
                    if (finalLength > 0)
                    {
                        slot.Lines[count] = Describe(slot.Bytes, carryOffset, finalLength, fileOffsetOfBufferZero, LinesRead + count + 1);
                        count++;
                    }

                    carryOffset += carryOverLength;
                    carryOverLength = 0;
                }

                LinesRead += count;
            }
            catch
            {
                // This reader still owns next. Preserve the parsing failure while
                // observing and releasing its speculative fill.
                await ReleaseUnusedPrefetchDuringUnwindAsync(next, nextFillTask);
                throw;
            }

            // Fold carry into the prefetched slot, or release it after the final chunk.
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
            // The reader still owns this slot when parsing fails.
            slot.Dispose();
            throw;
        }
    }

    // Folds carry into the prefetched slot before its source buffer is sorted in place.
    private async ValueTask FoldCarryIntoNextAsync(
        PooledBuffer next, Task<int> nextFillTask, byte[] previousBuffer, int carryOffset, int carryLength)
    {
        try
        {
            if (carryLength <= _reserve)
            {
                // Reserve accepts an ordinary partial line and optional CR without
                // waiting for the in-flight fill.
                int spanStart = _reserve - carryLength;
                if (carryLength > 0)
                {
                    Array.Copy(previousBuffer, carryOffset, next.Bytes, spanStart, carryLength);
                }

                _prefetchSlot = next;
                _prefetchCarryLength = carryLength;
                _prefetchSpanStart = spanStart;
                _prefetchFillTask = nextFillTask;

                // An ordinary fill count is comparable to _fillAmount.
                _prefetchCountIsCapped = false;
                return;
            }

            // Descriptor exhaustion can leave carry larger than Reserve. Rebuild the
            // slot around it, then retain any displaced prefetched bytes.
            int read = await nextFillTask;

            int keep = Math.Min(read, next.Bytes.Length - carryLength);
            int overflow = read - keep;
            if (overflow > 0)
            {
                // Preserve displaced bytes from the forward-only stream. Overflow is
                // bounded by one fill, so _pending has enough space.
                Array.Copy(next.Bytes, _reserve + keep, _pending, 0, overflow);
                _pendingLength = overflow;
            }

            if (keep > 0)
            {
                // Copy overflow first because these ranges can overlap.
                Array.Copy(next.Bytes, _reserve, next.Bytes, carryLength, keep);
            }

            if (carryLength > 0)
            {
                Array.Copy(previousBuffer, carryOffset, next.Bytes, 0, carryLength);
            }

            _prefetchSlot = next;
            _prefetchCarryLength = carryLength;
            _prefetchSpanStart = 0;

            // Count only retained bytes now; pending bytes count when delivered.
            _prefetchFillTask = Task.FromResult(keep);

            // A short retained count with overflow is not an EOF signal.
            _prefetchCountIsCapped = overflow > 0;
        }
        catch
        {
            next.Dispose();
            throw;
        }
    }

    // Observes and releases the final speculative fill. Propagate I/O failures so a
    // failed last read cannot make the sort appear successful.
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

    // During parsing failure, preserve the original exception while releasing this slot.
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

    // offset is buffer-relative, so add it to the file offset of buffer position zero.
    private static LineDescriptor Describe(byte[] buffer, int offset, int length, long fileOffsetOfBufferZero, long lineNumber)
    {
        ReadOnlySpan<byte> line = buffer.AsSpan(offset, length);
        if (!LineParser.TryParse(line, out long number, out int stringStart))
        {
            ReadOnlySpan<byte> preview = line.Length > PreviewMaxBytes ? line[..PreviewMaxBytes] : line;
            throw new MalformedLineException(fileOffsetOfBufferZero + offset, lineNumber, Encoding.UTF8.GetString(preview));
        }

        int stringOffset = offset + stringStart;
        int stringLength = LineDescriptor.StringLengthOf(offset, length, stringOffset);
        ulong prefix = LineDescriptor.BuildPrefix(buffer.AsSpan(stringOffset, stringLength));
        return new LineDescriptor(prefix, number, offset, length, stringOffset);
    }

    // Drain bytes already read from the forward-only stream before reading more. Callers
    // decide exhaustion from their own fill count.
    private async Task<int> FillAsync(byte[] buffer, int destinationOffset, int destinationLength, CancellationToken ct)
    {
        int fromPending = Math.Min(_pendingLength, destinationLength);
        if (fromPending > 0)
        {
            Array.Copy(_pending, 0, buffer, destinationOffset, fromPending);

            int remainingPending = _pendingLength - fromPending;
            if (remainingPending > 0)
            {
                // Keep this general if the pending-byte bound changes.
                Array.Copy(_pending, fromPending, _pending, 0, remainingPending);
            }

            _pendingLength = remainingPending;
        }

        int totalRead = fromPending;
        while (totalRead < destinationLength)
        {
            int read = await _input.ReadAsync(buffer.AsMemory(destinationOffset + totalRead, destinationLength - totalRead), ct);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    public async ValueTask DisposeAsync()
    {
        // Wait for a fill that still uses this pool slot and input stream.
        if (_prefetchFillTask is not null)
        {
            try
            {
                await _prefetchFillTask;
            }
            catch
            {
            }
        }

        _prefetchSlot?.Dispose();
        _prefetchSlot = null;
        _prefetchFillTask = null;
    }
}
