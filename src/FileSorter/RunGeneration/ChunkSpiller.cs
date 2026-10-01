using FileSorter.LineFormat;
using FileSorter.Startup;

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
            LineStager stager = new(new byte[_spillBufferSize], data => file.WriteAsync(data, ct));
            for (int i = 0; i < chunk.Count; i++)
            {
                LineDescriptor line = lines[i];
                if (!stager.TryAdd(buffer.AsSpan(line.Offset, line.Length)))
                {
                    await stager.AddAfterFlushAsync(buffer, line.Offset, line.Length);
                }
            }

            // Flush the final staged lines because no later line can trigger it.
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
}
