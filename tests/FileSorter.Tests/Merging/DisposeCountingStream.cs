using System.Diagnostics.CodeAnalysis;

namespace FileSorter.Tests.Merging;

/// A pass-through <see cref="Stream"/> that counts how many times each disposal path is
/// actually invoked, shared by <c>RunSliceStreamTests</c> and
/// <c>OutputSliceStreamTests</c>. A plain <see cref="MemoryStream"/> cannot catch a
/// double dispose, because its own <c>Dispose</c> is idempotent and calling it twice
/// looks identical to calling it once. This wrapper makes the call count observable.
internal sealed class DisposeCountingStream(Stream inner) : Stream
{
    public int DisposeCount { get; private set; }
    public int DisposeAsyncCount { get; private set; }

    public override bool CanRead  => inner.CanRead;
    public override bool CanSeek  => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length   => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        inner.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        inner.WriteAsync(buffer, ct);

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        DisposeCount++;
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    // Deliberately does not call base.DisposeAsync(): Stream's default implementation
    // calls Dispose() synchronously, which would run this class's Dispose(true) override
    // a second time and both count and dispose inner twice for one logical disposal,
    // defeating the one thing this fixture exists to count accurately.
    [SuppressMessage(
        "Usage", "CA2215:Dispose methods should call base class dispose",
        Justification = "See the method comment: calling it would double-count and " +
        "double-dispose inner through Stream's synchronous DisposeAsync fallback.")]
    public override async ValueTask DisposeAsync()
    {
        DisposeAsyncCount++;
        await inner.DisposeAsync();
    }
}
