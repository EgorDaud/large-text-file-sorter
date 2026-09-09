using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace FileSorter.Merging;

// A range-partitioned worker's write-only view of [start, end) in the preallocated output.
// It rejects writes beyond its slice and counts successful writes, detecting misplaced,
// duplicated, or dropped lines that a whole-file length check cannot see.
internal sealed class OutputSliceStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _end;
    private long _written;
    private bool _disposed;

    public OutputSliceStream(Stream inner, long start, long end)
    {
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, string.Create(CultureInfo.InvariantCulture,
                $"A slice cannot end ({end}) before it starts ({start})."));
        }

        _inner = inner;
        _inner.Position = start;
        _start = start;
        _end = end;
    }

    // Bytes written by this worker for the slice-length check.
    public long BytesWritten => _written;

    public override bool CanRead  => false;
    public override bool CanSeek  => false;
    public override bool CanWrite => true;
    public override long Length   => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        long at = _start + _written;
        if (at + buffer.Length > _end)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"A merge worker tried to write {buffer.Length} bytes at offset {at}, past the end of its own " +
                $"output slice [{_start}, {_end}). Its slice length was mispredicted, or the partition it was " +
                $"given does not match the runs it merged."));
        }

        // Count only completed writes so cancellation and write failures do not inflate the
        // downstream slice-length check.
        await _inner.WriteAsync(buffer, ct);
        _written += buffer.Length;
    }

    // Owns the inner stream; the guard makes disposal idempotent.
    [SuppressMessage(
        "Usage", "CA2215:Dispose methods should call base class dispose",
        Justification = "Stream.DisposeAsync's own default implementation is exactly " +
        "\"call Dispose() synchronously\" -- calling it here would run this class's " +
        "Dispose(true) override a second time and dispose _inner twice, which is the bug " +
        "this override exists to fix. The _disposed guard above is this override's " +
        "replacement for whatever base.DisposeAsync() would otherwise have provided.")]
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _inner.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
