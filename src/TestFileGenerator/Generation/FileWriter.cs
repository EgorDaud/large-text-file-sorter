namespace TestFileGenerator.Generation;

internal sealed class FileWriter
{
    private const int LinesPerReport = 4096;

    private readonly LineComposer _composer;
    private readonly long _targetBytes;
    private readonly byte[] _line = new byte[LineComposer.MaxComposedLineLength];

    public FileWriter(LineComposer composer, long targetBytes)
    {
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentOutOfRangeException.ThrowIfNegative(targetBytes);

        _composer = composer;
        _targetBytes = targetBytes;
    }

    public long Write(Stream output, CancellationToken ct, Action<long>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        long written = 0;
        int linesSinceReport = 0;

        while (_composer.TryComposeNext(_line, _targetBytes - written, out int lineBytes))
        {
            output.Write(_line, 0, lineBytes);
            written += lineBytes;

            if (++linesSinceReport == LinesPerReport)
            {
                linesSinceReport = 0;
                onProgress?.Invoke(written);

                ct.ThrowIfCancellationRequested();
            }
        }

        return written;
    }
}
