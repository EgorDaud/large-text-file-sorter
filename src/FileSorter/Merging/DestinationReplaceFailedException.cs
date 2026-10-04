namespace FileSorter.Merging;

internal sealed class DestinationReplaceFailedException(string outputPath, Exception inner)
    : Exception($"Output file '{outputPath}' could not be written: {inner.Message}", inner);
