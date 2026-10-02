using FileSorter.LineFormat;

namespace FileSorter.RunGeneration;

internal static class ChunkSorter
{
    private const int PrefixBucketCount = 256;
    private const int PrefixBytes = sizeof(ulong);

    // Bucket 0 is an ended string, so a proper prefix sorts before an extension starting with 0x00.
    private const int StringBucketCount = 257;

    // Caps passes on long shared runs; the comparator finishes whatever is left.
    private const int MaxRadixDepth = 16;

    private const int SmallRangeThreshold = 32;

    public static void Sort(Span<LineDescriptor> lines, byte[] buffer)
    {
        if (lines.Length <= 1)
        {
            return;
        }

        SortRange(lines, 0, buffer, new DescriptorComparer(buffer));
    }

    // Invariant: all keys in the range agree at every byte before depth.
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

        // Outside the loop: a stackalloc inside it would grow the frame on every iteration.
        Span<int> bucketStart = stackalloc int[StringBucketCount + 1];
        Span<int> next = stackalloc int[StringBucketCount];

        while (depth < MaxRadixDepth)
        {
            if (depth < PrefixBytes)
            {
                Span<int> prefixStart = bucketStart[..(PrefixBucketCount + 1)];
                if (CountByPrefix(lines, depth, prefixStart) >= 0)
                {
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
                // Strings ended inside the zero-padded prefix can still differ in length.
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

    // Turns counts into start offsets; returns the sole occupied bucket, or -1 if the range split.
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

    private static int StringKey(in LineDescriptor line, int depth, byte[] buffer) =>
        depth < line.StringLength ? buffer[line.StringOffset + depth] + 1 : 0;

    // In-place American flag permutation; bucketStart must come from the matching counting pass.
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

    private readonly struct DescriptorComparer(byte[] buffer) : IComparer<LineDescriptor>
    {
        public int Compare(LineDescriptor x, LineDescriptor y) => LineOrder.Compare(in x, buffer, in y, buffer);
    }
}
