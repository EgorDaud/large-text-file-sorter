using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace FileSorter.Merging;

// A forward-only view of [Start, End) in a stream it owns.
internal abstract class SliceStream : Stream
{
    private bool _disposed;

    protected SliceStream(Stream inner, long start, long end)
    {
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, string.Create(CultureInfo.InvariantCulture,
                $"A slice cannot end ({end}) before it starts ({start})."));
        }

        Inner = inner;
        Inner.Position = start;
        Start = start;
        End = end;
    }

    protected Stream Inner { get; }
    protected long Start { get; }
    protected long End { get; }

    public override bool CanSeek => false;
    public override long Length  => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    [SuppressMessage(
        "Usage", "CA2215:Dispose methods should call base class dispose",
        Justification = "Stream.DisposeAsync's own default implementation is exactly " +
        "\"call Dispose() synchronously\" -- calling it here would run this class's " +
        "Dispose(true) override a second time and dispose Inner twice, which is the bug " +
        "this override exists to fix. The _disposed guard above is this override's " +
        "replacement for whatever base.DisposeAsync() would otherwise have provided.")]
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Inner.DisposeAsync();
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
            Inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
