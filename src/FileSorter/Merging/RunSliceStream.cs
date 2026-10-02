namespace FileSorter.Merging;

// A read-only view of [start, end) in one run file. It presents the slice boundary as end
// of stream, leaving RunCursor's prefetch and carry state unchanged. Only ReadAsync and
// disposal are supported.
internal sealed class RunSliceStream : SliceStream
{
    private long _remaining;

    public RunSliceStream(Stream inner, long start, long end)
        : base(inner, start, end)
    {
        _remaining = end - start;
    }

    public override bool CanRead  => true;
    public override bool CanWrite => false;

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
        int read = await Inner.ReadAsync(buffer, ct);
        _remaining -= read;
        return read;
    }

    public override void Flush() => throw new NotSupportedException();
}
