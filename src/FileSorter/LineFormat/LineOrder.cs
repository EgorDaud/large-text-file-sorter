namespace FileSorter.LineFormat;

internal static class LineOrder
{
    public static int Compare(
        in LineDescriptor a, ReadOnlySpan<byte> bufferA,
        in LineDescriptor b, ReadOnlySpan<byte> bufferB)
    {
        // Unequal big-endian, zero-padded prefixes preserve byte order. Equal prefixes
        // still need a full comparison: padding can tie with real NUL bytes.
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

        // Raw bytes break numeric ties such as '007' and '+7', keeping output deterministic
        // across chunk boundaries and memory budgets.
        return bufferA.Slice(a.Offset, a.Length).SequenceCompareTo(bufferB.Slice(b.Offset, b.Length));
    }
}
