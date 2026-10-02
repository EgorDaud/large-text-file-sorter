using System.Diagnostics;

namespace FileSorter.LineFormat;

// Sync TryAdd with a separate async slow path, and a class not a struct: both measured faster in the caller's async loop.
internal sealed class LineStager
{
    private static readonly byte[] LineFeed = [(byte)'\n'];

    private readonly byte[] _buffer;
    private readonly Func<ReadOnlyMemory<byte>, ValueTask> _write;
    private int _length;

    public LineStager(byte[] buffer, Func<ReadOnlyMemory<byte>, ValueTask> write)
    {
        _buffer = buffer;
        _write = write;
    }

    public bool TryAdd(ReadOnlySpan<byte> line)
    {
        // Locals: the span copy would otherwise force field reloads.
        byte[] buffer = _buffer;
        int length = _length;
        if (line.Length >= buffer.Length - length)
        {
            Debug.Assert(length > 0 || line.Length + 1 > buffer.Length, "TryAdd failed on an empty buffer for a fitting line");
            return false;
        }

        line.CopyTo(buffer.AsSpan(length));
        length += line.Length;
        buffer[length] = (byte)'\n';
        _length = length + 1;
        return true;
    }

    public async ValueTask AddAfterFlushAsync(byte[] source, int offset, int length)
    {
        await FlushAsync();
        if (!TryAdd(source.AsSpan(offset, length)))
        {
            await _write(source.AsMemory(offset, length));
            await _write(LineFeed);
        }
    }

    public ValueTask FlushAsync()
    {
        if (_length == 0)
        {
            return ValueTask.CompletedTask;
        }

        // The caller must await this write before the next TryAdd reuses the buffer.
        ReadOnlyMemory<byte> pending = _buffer.AsMemory(0, _length);
        _length = 0;
        return _write(pending);
    }
}
