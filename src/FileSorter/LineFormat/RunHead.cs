namespace FileSorter.LineFormat;

// The merge's view of one run's current line. KWayMerge refreshes a head before replaying it;
// a null buffer marks an exhausted leaf. It lives here, beside LineDescriptor, so MemoryPlan
// can price the loser tree from the real struct size without depending on Merging.
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
