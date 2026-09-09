using FileSorter.LineFormat;

namespace FileSorter.RunGeneration;

internal static class ChunkSorter
{
    // One bucket per byte keeps the radix small; another pass handles sparse prefixes.
    private const int PrefixBucketCount = 256;

    // The cached prefix covers these bytes before the radix reads the chunk buffer.
    private const int PrefixBytes = sizeof(ulong);

    // Bucket 0 represents an ended string; buckets 1–256 represent byte values. The
    // separate end marker keeps a proper prefix before an extension beginning with 0x00.
    private const int StringBucketCount = 257;

    // Bound radix work on long common prefixes; LineOrder preserves the final order.
    // Depth 16 avoids excessive passes on degenerate input.
    private const int MaxRadixDepth = 16;

    // Small ranges use the BCL sort to avoid another radix counting pass.
    private const int SmallRangeThreshold = 32;

    /// Sorts cached prefix bytes, then string bytes, with an MSD radix. Bucket order
    /// follows LineOrder; final ranges use its full comparator, including raw-byte ties.
    public static void Sort(Span<LineDescriptor> lines, byte[] buffer)
    {
        if (lines.Length <= 1)
        {
            return;
        }

        SortRange(lines, 0, buffer, new DescriptorComparer(buffer));
    }

    /// Sorts a range whose keys agree at every byte before <paramref name="depth"/>.
    private static void SortRange(
        Span<LineDescriptor> lines, int depth, byte[] buffer, DescriptorComparer comparer)
    {
        if (lines.Length <= SmallRangeThreshold)
        {
            if (lines.Length > 1)
            {
                lines.Sort(comparer);
            }

            return;
        }

        // Reuse the widest radix arrays in this frame. Counting passes clear them;
        // allocating them in the loop would grow stack use until the method returns.
        Span<int> bucketStart = stackalloc int[StringBucketCount + 1];
        Span<int> next = stackalloc int[StringBucketCount];

        while (depth < MaxRadixDepth)
        {
            if (depth < PrefixBytes)
            {
                Span<int> prefixStart = bucketStart[..(PrefixBucketCount + 1)];
                if (CountByPrefix(lines, depth, prefixStart) >= 0)
                {
                    // No split means no permutation or recursion is needed.
                    depth++;
                    continue;
                }

                PermuteByPrefix(lines, depth, prefixStart, next[..PrefixBucketCount]);
                for (int b = 0; b < PrefixBucketCount; b++)
                {
                    int start = prefixStart[b];
                    int count = prefixStart[b + 1] - start;
                    if (count > 1)
                    {
                        SortRange(lines.Slice(start, count), depth + 1, buffer, comparer);
                    }
                }

                return;
            }

            int single = CountByStringByte(lines, depth, buffer, bucketStart);
            if (single == 0)
            {
                // Ended strings can still differ under padded prefixes, so LineOrder
                // must finish the range.
                break;
            }

            if (single > 0)
            {
                depth++;
                continue;
            }

            PermuteByStringByte(lines, depth, buffer, bucketStart, next);

            int ended = bucketStart[1];
            if (ended > 1)
            {
                lines[..ended].Sort(comparer);
            }

            for (int b = 1; b < StringBucketCount; b++)
            {
                int start = bucketStart[b];
                int count = bucketStart[b + 1] - start;
                if (count > 1)
                {
                    SortRange(lines.Slice(start, count), depth + 1, buffer, comparer);
                }
            }

            return;
        }

        lines.Sort(comparer);
    }

    /// Writes bucket start offsets, ending with lines.Length. Returns the sole
    /// occupied bucket, or -1 when the range split.
    private static int CountByPrefix(
        ReadOnlySpan<LineDescriptor> lines, int depth, Span<int> bucketStart)
    {
        int shift = 56 - (8 * depth);
        bucketStart.Clear();
        for (int i = 0; i < lines.Length; i++)
        {
            bucketStart[(int)((lines[i].Prefix >> shift) & 0xFF) + 1]++;
        }

        return Accumulate(bucketStart, PrefixBucketCount, lines.Length);
    }

    private static int CountByStringByte(
        ReadOnlySpan<LineDescriptor> lines, int depth, byte[] buffer, Span<int> bucketStart)
    {
        bucketStart.Clear();
        for (int i = 0; i < lines.Length; i++)
        {
            bucketStart[StringKey(in lines[i], depth, buffer) + 1]++;
        }

        return Accumulate(bucketStart, StringBucketCount, lines.Length);
    }

    /// Converts counts to start offsets and returns the sole occupied bucket, or -1.
    private static int Accumulate(Span<int> bucketStart, int bucketCount, int length)
    {
        int single = -1;
        for (int b = 0; b < bucketCount; b++)
        {
            if (bucketStart[b + 1] == length)
            {
                single = b;
            }

            bucketStart[b + 1] += bucketStart[b];
        }

        return single;
    }

    // Use 0 for an ended string and byte + 1 for content after the cached prefix.
    private static int StringKey(in LineDescriptor line, int depth, byte[] buffer) =>
        depth < line.StringLength ? buffer[line.StringOffset + depth] + 1 : 0;

    // American flag permutation uses O(n) moves per level without a second
    // descriptor array. next[b] tracks each bucket's next free slot; displaced
    // items follow their cycles until the current bucket receives its own item.
    // bucketStart must come from the matching counting pass. Skip unsplit ranges.
    private static void PermuteByPrefix(
        Span<LineDescriptor> lines, int depth, Span<int> bucketStart, Span<int> next)
    {
        int shift = 56 - (8 * depth);
        bucketStart[..PrefixBucketCount].CopyTo(next);

        for (int b = 0; b < PrefixBucketCount; b++)
        {
            int end = bucketStart[b + 1];
            while (next[b] < end)
            {
                LineDescriptor item = lines[next[b]];
                int bucket = (int)((item.Prefix >> shift) & 0xFF);
                while (bucket != b)
                {
                    int dest = next[bucket]++;
                    (lines[dest], item) = (item, lines[dest]);
                    bucket = (int)((item.Prefix >> shift) & 0xFF);
                }

                lines[next[b]] = item;
                next[b]++;
            }
        }
    }

    private static void PermuteByStringByte(
        Span<LineDescriptor> lines, int depth, byte[] buffer, Span<int> bucketStart, Span<int> next)
    {
        bucketStart[..StringBucketCount].CopyTo(next);

        for (int b = 0; b < StringBucketCount; b++)
        {
            int end = bucketStart[b + 1];
            while (next[b] < end)
            {
                LineDescriptor item = lines[next[b]];
                int bucket = StringKey(in item, depth, buffer);
                while (bucket != b)
                {
                    int dest = next[bucket]++;
                    (lines[dest], item) = (item, lines[dest]);
                    bucket = StringKey(in item, depth, buffer);
                }

                lines[next[b]] = item;
                next[b]++;
            }
        }
    }

    // This comparer is valid only for descriptors from the captured chunk buffer.
    private readonly struct DescriptorComparer(byte[] buffer) : IComparer<LineDescriptor>
    {
        public int Compare(LineDescriptor x, LineDescriptor y) => LineOrder.Compare(in x, buffer, in y, buffer);
    }
}
