using System.Diagnostics;

namespace FileSorter.LineFormat;

// Batches newline-terminated lines into one caller-supplied buffer so the caller issues one
// write per buffer instead of one per line. The hot path is TryAdd, a plain copy; the
// write callback runs only on the slow path (a full buffer or an oversized line), so it is
// never invoked per line. The slow path stays in its own async method on purpose: an
// await inside the caller's per-line loop measurably slows that loop.
// A class rather than a struct for the same reason: a struct hoisted into the caller's
// async state machine is slower than one heap object allocated once per spill or merge.
internal sealed class LineStager
{
    private static readonly byte[] LineFeed = [(byte)'\n'];

    private readonly byte[] _buffer;
    private readonly Func<ReadOnlyMemory<byte>, ValueTask> _write;
    private int _length;

    // The buffer is the caller's, sized by the memory plan. The callback is allocated once
    // by the caller and owns the destination stream, cancellation and any timing.
    public LineStager(byte[] buffer, Func<ReadOnlyMemory<byte>, ValueTask> write)
    {
        _buffer = buffer;
        _write = write;
    }

    // Stages the line and its LF if both fit. On false the buffer is unchanged and the
    // caller must await AddAfterFlushAsync.
    public bool TryAdd(ReadOnlySpan<byte> line)
    {
        // Read each field once: the copy below would otherwise force reloads.
        byte[] buffer = _buffer;
        int length = _length;
        if (line.Length >= buffer.Length - length)
        {
            // A line that fits an empty buffer fails only because something is already staged.
            Debug.Assert(length > 0 || line.Length + 1 > buffer.Length, "TryAdd failed on an empty buffer for a fitting line");
            return false;
        }

        line.CopyTo(buffer.AsSpan(length));
        length += line.Length;
        buffer[length] = (byte)'\n';
        _length = length + 1;
        return true;
    }

    // Slow path after TryAdd returned false: flushes what is staged, then stages the line,
    // or writes a line larger than the whole buffer directly (one write for it, one for LF).
    public async ValueTask AddAfterFlushAsync(byte[] source, int offset, int length)
    {
        await FlushAsync();
        if (!TryAdd(source.AsSpan(offset, length)))
        {
            await _write(source.AsMemory(offset, length));
            await _write(LineFeed);
        }
    }

    // Writes whatever is staged in one call; a no-op when nothing is.
    public ValueTask FlushAsync()
    {
        if (_length == 0)
        {
            return ValueTask.CompletedTask;
        }

        // The bytes stay valid until the next TryAdd, after the caller has awaited this write.
        ReadOnlyMemory<byte> pending = _buffer.AsMemory(0, _length);
        _length = 0;
        return _write(pending);
    }
}
