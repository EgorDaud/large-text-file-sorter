namespace FileSorter.Tests.Properties;

internal sealed class LimitedReadStream(Stream inner, int maxBytesPerRead, Random random) : Stream
{
    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    // ChunkReader and RunCursor only call this overload.
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int cap = random.Next(1, maxBytesPerRead + 1);
        int length = Math.Min(buffer.Length, cap);
        return inner.ReadAsync(buffer[..length], cancellationToken);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
