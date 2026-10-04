namespace FileSorter.LineFormat;

internal readonly struct RunHead
{
    public readonly byte[]? Buffer;
    public readonly LineDescriptor Descriptor;

    public RunHead(byte[] buffer, LineDescriptor descriptor)
    {
        Buffer = buffer;
        Descriptor = descriptor;
    }

    public bool IsAlive => Buffer is not null;
}
