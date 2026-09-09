using System.Buffers;

namespace TestFileGenerator.Generation;

/// Writes complete lines until the next line would exceed the target.
internal static class FileWriter
{
    // Avoid a callback for every line on large files.
    private const int LinesPerReport = 4096;

    /// Bytes written, always at or below targetBytes.
    public static long Write(
        Stream output,
        LineComposer composer,
        long targetBytes,
        Action<long>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentOutOfRangeException.ThrowIfNegative(targetBytes);

        byte[] line = ArrayPool<byte>.Shared.Rent(LineComposer.MaxComposedLineLength);
        try
        {
            long written = 0;
            int linesSinceReport = 0;

            while (composer.TryComposeNext(line, targetBytes - written, out int lineBytes))
            {
                output.Write(line, 0, lineBytes);
                written += lineBytes;

                if (++linesSinceReport == LinesPerReport)
                {
                    linesSinceReport = 0;
                    onProgress?.Invoke(written);
                }
            }

            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(line);
        }
    }
}
