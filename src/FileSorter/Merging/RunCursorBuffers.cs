using FileSorter.LineFormat;
using FileSorter.Planning;

namespace FileSorter.Merging;

internal readonly record struct RunCursorBuffers(byte[] FirstWindow, byte[] SecondWindow, LineDescriptor[] Descriptors)
{
    public static RunCursorBuffers Create(MemoryPlan plan) => new(
        new byte[plan.ReadAheadBufferSize],
        new byte[plan.ReadAheadBufferSize],
        new LineDescriptor[plan.ReadAheadDescriptorCapacity]);
}
