using FileSorter.LineFormat;

namespace FileSorter.Tests.LineFormat;

/// <summary>
/// Drives <see cref="LineCursor"/> and <see cref="LineParser"/> together over a buffer,
/// the way run generation does, so the descriptor and ordering tests exercise real
/// descriptors instead of hand-rolled ones that could silently diverge from what the
/// cursor and parser actually produce.
/// </summary>
internal static class DescriptorFixture
{
    public static List<LineDescriptor> Build(byte[] buffer, int maxLineLength = 64 * 1024)
    {
        List<LineDescriptor> descriptors = [];
        LineCursor cursor = new(buffer, maxLineLength);

        while (cursor.TryReadLine(out int offset, out int length))
        {
            if (!LineDescriptor.TryCreate(buffer, offset, length, out LineDescriptor descriptor))
            {
                throw new FormatException($"Line at offset {offset} does not match the settled grammar.");
            }

            descriptors.Add(descriptor);
        }

        return descriptors;
    }
}
