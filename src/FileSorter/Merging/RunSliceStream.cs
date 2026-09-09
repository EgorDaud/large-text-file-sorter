using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace FileSorter.Merging;

// A read-only view of [start, end) in one run file. It presents the slice boundary as end
// of stream, leaving RunCursor's prefetch and carry state unchanged. Only its required
// read and disposal operations are supported.
internal sealed class RunSliceStream : Stream
{
    private readonly Stream _inner;
    private long _remaining;
    private bool _disposed;

    public RunSliceStream(Stream inner, long start, long end)
    {
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, string.Create(CultureInfo.InvariantCulture,
                $"A slice cannot end ({end}) before it starts ({start})."));
        }

        _inner = inner;
        _inner.Position = start;
        _remaining = end - start;
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_remaining <= 0)
        {
            return ValueTask.FromResult(0);
        }

        int want = (int)Math.Min(buffer.Length, _remaining);
        return ReadCoreAsync(buffer[..want], ct);
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int read = await _inner.ReadAsync(buffer, ct);
        _remaining -= read;
        return read;
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

    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
