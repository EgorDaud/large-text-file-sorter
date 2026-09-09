using FileSorter.LineFormat;

namespace FileSorter.Merging;

internal readonly record struct RunCursorBuffers(byte[] FirstWindow, byte[] SecondWindow, LineDescriptor[] Descriptors);
