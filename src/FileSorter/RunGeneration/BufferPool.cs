using System.Threading.Channels;
using FileSorter.LineFormat;

namespace FileSorter.RunGeneration;

internal sealed class BufferPool
{
    private readonly byte[][] _bytes;
    private readonly LineDescriptor[][] _lines;
    private readonly bool[] _outstanding;
    private readonly Channel<int> _slots;

    private int _outstandingCount;

    public BufferPool(int bufferSize, int descriptorCapacity, int capacity)
    {
        BufferSize = bufferSize;
        _bytes = new byte[capacity][];
        _lines = new LineDescriptor[capacity][];
        _outstanding = new bool[capacity];

        // The bounded channel holds every available slot. Acquiring reads one and
        // releasing returns it, which limits outstanding buffers to capacity.
        _slots = Channel.CreateBounded<int>(capacity);
        for (int slot = 0; slot < capacity; slot++)
        {
            _bytes[slot] = new byte[bufferSize];
            _lines[slot] = new LineDescriptor[descriptorCapacity];
            _slots.Writer.TryWrite(slot);
        }
    }

    public int BufferSize { get; }

    public int DescriptorCapacity => _lines.Length == 0 ? 0 : _lines[0].Length;

    public int Outstanding => Volatile.Read(ref _outstandingCount);

    public async ValueTask<PooledBuffer> AcquireAsync(CancellationToken ct)
    {
        int slot = await _slots.Reader.ReadAsync(ct);
        _outstanding[slot] = true;
        Interlocked.Increment(ref _outstandingCount);
        return new PooledBuffer(this, slot, _bytes[slot], _lines[slot]);
    }

    internal void Release(int slot)
    {
        if (!_outstanding[slot])
        {
            throw new InvalidOperationException(
                $"Slot {slot} is not outstanding: either it was already released, or this pool never issued it.");
        }

        _outstanding[slot] = false;
        Interlocked.Decrement(ref _outstandingCount);
        _slots.Writer.TryWrite(slot);
    }
}

internal readonly struct PooledBuffer : IDisposable
{
    private readonly BufferPool _pool;
    private readonly int _slot;

    public byte[]           Bytes { get; }
    public LineDescriptor[] Lines { get; }

    internal PooledBuffer(BufferPool pool, int slot, byte[] bytes, LineDescriptor[] lines)
    {
        _pool = pool;
        _slot = slot;
        Bytes = bytes;
        Lines = lines;
    }

    public void Dispose() => _pool.Release(_slot);
}
