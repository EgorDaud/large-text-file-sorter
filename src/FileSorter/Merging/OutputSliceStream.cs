using System.Globalization;

namespace FileSorter.Merging;

// The per-slice write count catches misplaced or dropped lines that a whole-file length check cannot.
internal sealed class OutputSliceStream : SliceStream
{
    public OutputSliceStream(Stream inner, long start, long end)
        : base(inner, start, end)
    {
    }

    public long BytesWritten { get; private set; }

    public override bool CanRead  => false;
    public override bool CanWrite => true;

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        long at = Start + BytesWritten;
        if (at + buffer.Length > End)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"A merge worker tried to write {buffer.Length} bytes at offset {at}, past the end of its own " +
                $"output slice [{Start}, {End}). Its slice length was mispredicted, or the partition it was " +
                $"given does not match the runs it merged."));
        }

        // Counted after the await so a failed or cancelled write is not counted.
        await Inner.WriteAsync(buffer, ct);
        BytesWritten += buffer.Length;
    }

    public override void Flush() => Inner.Flush();
}
