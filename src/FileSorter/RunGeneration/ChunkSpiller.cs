using System.Diagnostics;
using FileSorter.LineFormat;
using FileSorter.Startup;

namespace FileSorter.RunGeneration;

internal sealed class ChunkSpiller
{
    private static readonly byte[] LineTerminator = [(byte)'\n'];

    private readonly TemporaryRunSet _runs;
    private readonly int _spillBufferSize;

    public ChunkSpiller(TemporaryRunSet runs, int spillBufferSize)
    {
        _runs = runs;
        _spillBufferSize = spillBufferSize;
    }

    // Sorts and writes one run, then returns the chunk's pool slot even on failure.
    public async Task<string> SpillAsync(Chunk chunk, CancellationToken ct)
    {
        try
        {
            byte[] buffer = chunk.Buffer.Bytes;
            LineDescriptor[] lines = chunk.Buffer.Lines;
            ChunkSorter.Sort(lines.AsSpan(0, chunk.Count), buffer);

            string path = _runs.CreateRunPath();

            // Each output line contributes its bytes and one newline.
            long totalBytes = 0;
            for (int i = 0; i < chunk.Count; i++)
            {
                totalBytes += lines[i].Length + 1;
            }

            // The staging buffer below is the only write buffer in the memory plan, so
            // FileStream uses one byte. CreateNew keeps an unexpected path collision visible.
            await using FileStream file = new(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            // Preallocate the exact run length to avoid incremental file growth. Verify
            // the final position below because SetLength leaves zero-filled bytes on a short write.
            file.SetLength(totalBytes);

            // A spill needs its own staging buffer because one ChunkSpiller serves
            // concurrent calls. Each flush writes complete lines and observes cancellation.
            byte[] staging = new byte[_spillBufferSize];
            int stagedLength = 0;
            for (int i = 0; i < chunk.Count; i++)
            {
                LineDescriptor line = lines[i];
                if (!TryStageLine(staging, ref stagedLength, buffer.AsSpan(line.Offset, line.Length)))
                {
                    stagedLength = await StageLineAsync(file, staging, stagedLength, buffer, line.Offset, line.Length, ct);
                }
            }

            // Flush the final staged lines because no later line can trigger it.
            if (stagedLength > 0)
            {
                await file.WriteAsync(staging.AsMemory(0, stagedLength), ct);
            }

            // A mismatched length would leave a zero-filled tail after SetLength.
            if (file.Position != totalBytes)
            {
                throw new InvalidOperationException(
                    $"ChunkSpiller wrote {file.Position} bytes to '{path}' but predicted {totalBytes}.");
            }

            return path;
        }
        finally
        {
            chunk.Buffer.Dispose();
        }
    }

    // Stages a complete line without flushing. On failure it leaves the buffer unchanged
    // so the caller can flush and retry.
    private static bool TryStageLine(byte[] staging, ref int stagedLength, ReadOnlySpan<byte> line)
    {
        int needed = line.Length + 1;
        if (stagedLength + needed > staging.Length)
        {
            return false;
        }

        line.CopyTo(staging.AsSpan(stagedLength));
        stagedLength += line.Length;
        staging[stagedLength] = LineTerminator[0];
        stagedLength++;
        return true;
    }

    // Flushes the buffer, or writes a line larger than the staging buffer directly.
    private static async ValueTask<int> StageLineAsync(
        FileStream file, byte[] staging, int stagedLength, byte[] source, int offset, int length, CancellationToken ct)
    {
        int needed = length + 1;
        if (needed > staging.Length)
        {
            if (stagedLength > 0)
            {
                await file.WriteAsync(staging.AsMemory(0, stagedLength), ct);
            }

            await file.WriteAsync(source.AsMemory(offset, length), ct);
            await file.WriteAsync(LineTerminator, ct);
            return 0;
        }

        // TryStageLine failed only because this non-empty buffer lacked room.
        Debug.Assert(stagedLength > 0, "TryStageLine returning false means something was already staged");
        await file.WriteAsync(staging.AsMemory(0, stagedLength), ct);

        source.AsSpan(offset, length).CopyTo(staging);
        staging[length] = LineTerminator[0];
        return length + 1;
    }
}
