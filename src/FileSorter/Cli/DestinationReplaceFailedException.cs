namespace FileSorter.Cli;

// A destination the empty-input or single-run placement could not replace. ConsoleRun maps it to
// exit 3, the code an unwritable output directory already gets; merge write failures stay exit 5.
internal sealed class DestinationReplaceFailedException(string outputPath, Exception inner)
    : Exception($"Output file '{outputPath}' could not be written: {inner.Message}", inner);
