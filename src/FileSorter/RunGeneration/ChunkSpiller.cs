using FileSorter.Infrastructure;
using FileSorter.LineFormat;

namespace FileSorter.RunGeneration;

internal sealed class ChunkSpiller
{
    private readonly TemporaryRunSet _runs;
    private readonly int _spillBufferSize;

    public ChunkSpiller(TemporaryRunSet runs, int spillBufferSize)
    {
        _runs = runs;
        _spillBufferSize = spillBufferSize;
    }

    public async Task<string> SpillAsync(Chunk chunk, CancellationToken ct)
    {
        try
        {
            // Strategies start spills even after cancellation so the finally releases the slot.
            ct.ThrowIfCancellationRequested();

            byte[] buffer = chunk.Buffer.Bytes;
            LineDescriptor[] lines = chunk.Buffer.Lines;
            ChunkSorter.Sort(lines.AsSpan(0, chunk.Count), buffer);

            string path = _runs.CreateRunPath();

            long totalBytes = 0;
            for (int i = 0; i < chunk.Count; i++)
            {
                totalBytes += lines[i].Length + 1;
            }

            // Unbuffered: the staging buffer is the only write buffer the memory plan accounts for.
            await using FileStream file = new(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: FileStreams.Unbuffered,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            file.SetLength(totalBytes);

            // Per-call buffer: one ChunkSpiller serves concurrent spills.
            LineStager stager = CreateFileStager(new byte[_spillBufferSize], file, ct);
            for (int i = 0; i < chunk.Count; i++)
            {
                LineDescriptor line = lines[i];
                if (!stager.TryAdd(buffer.AsSpan(line.Offset, line.Length)))
                {
                    await stager.AddAfterFlushAsync(buffer, line.Offset, line.Length);
                }
            }

            await stager.FlushAsync();

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

    private static LineStager CreateFileStager(byte[] buffer, FileStream file, CancellationToken ct) =>
        new(buffer, data => file.WriteAsync(data, ct));
}
