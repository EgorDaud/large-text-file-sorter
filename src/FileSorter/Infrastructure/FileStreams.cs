namespace FileSorter.Infrastructure;

internal static class FileStreams
{
    // bufferSize 1 disables FileStream's own buffer; our buffers are already budgeted in the plan.
    internal const int Unbuffered = 1;
}
