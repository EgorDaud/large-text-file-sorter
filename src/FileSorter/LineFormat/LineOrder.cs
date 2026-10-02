namespace FileSorter.LineFormat;

internal static class LineOrder
{
    public static int Compare(
        in LineDescriptor a, ReadOnlySpan<byte> bufferA,
        in LineDescriptor b, ReadOnlySpan<byte> bufferB)
    {
        // Equal prefixes still need the full compare: zero padding ties with real NUL bytes.
        if (a.Prefix != b.Prefix)
        {
            return a.Prefix < b.Prefix ? -1 : 1;
        }

        int stringComparison = bufferA.Slice(a.StringOffset, a.StringLength)
            .SequenceCompareTo(bufferB.Slice(b.StringOffset, b.StringLength));
        if (stringComparison != 0)
        {
            return stringComparison;
        }

        int numberComparison = a.Number.CompareTo(b.Number);
        if (numberComparison != 0)
        {
            return numberComparison;
        }

        // Raw bytes break ties like '007' vs '+7', so output is identical across chunk sizes.
        return bufferA.Slice(a.Offset, a.Length).SequenceCompareTo(bufferB.Slice(b.Offset, b.Length));
    }
}
