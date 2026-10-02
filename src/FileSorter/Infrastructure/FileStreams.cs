namespace FileSorter.Infrastructure;

internal static class FileStreams
{
    // Every FileStream passes this as bufferSize, which disables its internal buffer (D12). Read-ahead
    // and staging buffers are budgeted in the plan, and a second hidden buffer would copy the data twice.
    internal const int Unbuffered = 1;
}
