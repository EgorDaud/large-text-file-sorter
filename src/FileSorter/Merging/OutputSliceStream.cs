using System.Globalization;

namespace FileSorter.Merging;

// A range-partitioned worker's write-only view of [start, end) in the preallocated output.
// It rejects writes beyond its slice and counts successful writes, detecting misplaced,
// duplicated, or dropped lines that a whole-file length check cannot see.
internal sealed class OutputSliceStream : SliceStream
{
    public OutputSliceStream(Stream inner, long start, long end)
        : base(inner, start, end)
    {
    }

    // Bytes written by this worker for the slice-length check.
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

        // Count only completed writes so cancellation and write failures do not inflate the
        // downstream slice-length check.
        await Inner.WriteAsync(buffer, ct);
        BytesWritten += buffer.Length;
    }

    public override void Flush() => Inner.Flush();
}
