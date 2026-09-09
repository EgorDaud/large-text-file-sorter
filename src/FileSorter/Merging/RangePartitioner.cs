using System.Runtime.ExceptionServices;
using System.Text;
using FileSorter.LineFormat;
using Microsoft.Win32.SafeHandles;

namespace FileSorter.Merging;

// Splits one merge into disjoint key ranges, recorded as byte offsets for each run. It
// samples byte-position quantiles for splitter keys, then binary-searches each sorted run
// for the first line whose full LineOrder key is at least that splitter. Equal lines remain
// together because the boundary is before the first equal line.
//
// This runs before merge buffers are allocated. Its bound is SampleLineByteBudget, one
// max(FirstProbeBytes, maxLineLength + 2) probe per concurrent task, and
// runCount x (workerCount + 1) offsets.
internal static class RangePartitioner
{
    private const byte Newline = (byte)'\n';

    // Long enough to be actionable, short enough not to dump a whole line into a console.
    private const int PreviewMaxBytes = 128;

    // Initial probe size; the fill loop handles lines that exceed it.
    private const int FirstProbeBytes = 8 * 1024;

    // Fixed sample count keeps splitter quality independent of the configured line limit.
    // SampleLineByteBudget limits retained bytes.
    private const int TargetSampleLines = 4096;
    private const int SampleLineByteBudget = 16 * 1024 * 1024;

    // Returns null when no partition can be sampled. parallelism also bounds probe-buffer
    // allocation while these synchronous I/O loops run.
    public static RangePartition? Locate(
        IReadOnlyList<string> runPaths, int workerCount, int maxLineLength, int parallelism, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 2);

        int runCount = runPaths.Count;
        long[] lengths = new long[runCount];
        long[] runStarts = new long[runCount + 1];
        for (int r = 0; r < runCount; r++)
        {
            lengths[r] = new FileInfo(runPaths[r]).Length;
            runStarts[r + 1] = runStarts[r] + lengths[r];
        }

        long totalBytes = runStarts[runCount];
        if (totalBytes == 0)
        {
            return null;
        }

        int probeSize = Math.Max(FirstProbeBytes, maxLineLength + 2);
        SampleLine[] splitters = ChooseSplitters(
            runPaths, lengths, runStarts, totalBytes, workerCount, maxLineLength, probeSize, parallelism, ct);
        if (splitters.Length == 0)
        {
            return null;
        }

        long[][] offsets = new long[runCount][];
        RunInParallel(runCount, probeSize, parallelism, (r, probe) =>
        {
            using SafeFileHandle handle = File.OpenHandle(
                runPaths[r], FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);

            long length = lengths[r];
            long[] runOffsets = new long[workerCount + 1];
            runOffsets[workerCount] = length;

            // Sorted splitters make offsets monotone and shrink each later search interval.
            long lower = 0;
            for (int s = 0; s < splitters.Length; s++)
            {
                lower = LocateFirstAtOrAbove(
                    handle, length, lower, splitters[s], probe, maxLineLength, runPaths[r]);
                runOffsets[s + 1] = lower;
            }

            offsets[r] = runOffsets;
        }, ct);

        return RangePartition.From(offsets, lengths, workerCount, totalBytes);
    }

    // Splitter keys retain their descriptors. Repeated keys produce empty slices safely.
    private static SampleLine[] ChooseSplitters(
        IReadOnlyList<string> runPaths, long[] lengths, long[] runStarts, long totalBytes,
        int workerCount, int maxLineLength, int probeSize, int parallelism, CancellationToken ct)
    {
        int sampleCount = TargetSampleLines;
        long retainedBytes = 0;

        SampleLine?[] drawn = new SampleLine?[sampleCount];
        RunInParallel(sampleCount, probeSize, parallelism, (i, probe) =>
        {
            // Sample the midpoint of each equal byte segment in the concatenated runs.
            long globalPosition = ((2 * (long)i + 1) * totalBytes) / (2L * sampleCount);
            int run = FindRun(runStarts, globalPosition);
            long position = globalPosition - runStarts[run];

            using SafeFileHandle handle = File.OpenHandle(
                runPaths[run], FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);

            long lineStart = AlignAtOrAfter(handle, lengths[run], position, probe, runPaths[run]);
            if (lineStart >= lengths[run])
            {
                // The position lies in the last line, with no later line start to sample.
                return;
            }

            int length = ReadLineAt(handle, lineStart, probe, maxLineLength, runPaths[run]);

            // Read and parse every draw, but retain only lines within the byte budget.
            // Interlocked.Add tests the post-add total before copying bytes, making the
            // retained-byte bound exact.
            if (Interlocked.Add(ref retainedBytes, length) > SampleLineByteBudget)
            {
                return;
            }

            byte[] bytes = probe.AsSpan(0, length).ToArray();

            // Build here so malformed-line diagnostics retain their path and byte offset.
            drawn[i] = new SampleLine(bytes, Describe(bytes, 0, bytes.Length, lineStart, runPaths[run]));
        }, ct);

        List<SampleLine> sample = new(sampleCount);
        foreach (SampleLine? line in drawn)
        {
            if (line is { } value)
            {
                sample.Add(value);
            }
        }

        if (sample.Count == 0)
        {
            return [];
        }

        sample.Sort(static (a, b) => LineOrder.Compare(in a.Descriptor, a.Bytes, in b.Descriptor, b.Bytes));

        SampleLine[] splitters = new SampleLine[workerCount - 1];
        for (int w = 1; w < workerCount; w++)
        {
            splitters[w - 1] = sample[(int)((long)sample.Count * w / workerCount)];
        }

        return splitters;
    }

    // Finds the first line in [lower, length) with a key at least splitter, or length.
    // AtOrAbove is monotone over byte positions because the run is sorted.
    private static long LocateFirstAtOrAbove(
        SafeFileHandle handle, long length, long lower, in SampleLine splitter,
        byte[] probe, int maxLineLength, string runPath)
    {
        long low = lower;
        long high = length;
        while (low < high)
        {
            long mid = low + ((high - low) / 2);
            if (AtOrAbove(handle, length, mid, in splitter, probe, maxLineLength, runPath))
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        // Align the lower-bound position so every partition offset starts a line.
        return AlignAtOrAfter(handle, length, low, probe, runPath);
    }

    private static bool AtOrAbove(
        SafeFileHandle handle, long length, long position, in SampleLine splitter,
        byte[] probe, int maxLineLength, string runPath)
    {
        long lineStart = AlignAtOrAfter(handle, length, position, probe, runPath);
        if (lineStart >= length)
        {
            // No later line means this splitter's boundary is the run length.
            return true;
        }

        int lineLength = ReadLineAt(handle, lineStart, probe, maxLineLength, runPath);
        LineDescriptor descriptor = Describe(probe, 0, lineLength, lineStart, runPath);

        // Use the merge's full ordering so partition boundaries match emitted order.
        return LineOrder.Compare(in descriptor, probe, in splitter.Descriptor, splitter.Bytes) >= 0;
    }

    // Finds the first line start at or after position, or length inside the final line.
    private static long AlignAtOrAfter(
        SafeFileHandle handle, long length, long position, byte[] probe, string runPath)
    {
        if (position <= 0)
        {
            return 0;
        }

        long scan = position - 1;
        if (scan >= length)
        {
            return length;
        }

        int newline = FillToNewline(handle, scan, probe, out int filled);
        if (newline >= 0)
        {
            return scan + newline + 1;
        }

        if (filled < probe.Length)
        {
            return length;
        }

        // A valid line must terminate within this maxLineLength + 2 probe.
        throw Malformed(scan, probe, filled, runPath);
    }

    // Reads content into probe without stripping CR. Runs use LF terminators, so a preceding
    // CR is content and must match RunCursor's key.
    private static int ReadLineAt(
        SafeFileHandle handle, long lineStart, byte[] probe, int maxLineLength, string runPath)
    {
        int newline = FillToNewline(handle, lineStart, probe, out int filled);
        int contentLength = newline >= 0 ? newline : filled;

        if (contentLength > maxLineLength)
        {
            throw Malformed(lineStart, probe, filled, runPath);
        }

        return contentLength;
    }

    // Reads forward from `from` into probe until a line feed appears, the probe is full, or
    // the file ends. Returns that line feed's index in probe, or -1.
    private static int FillToNewline(SafeFileHandle handle, long from, byte[] probe, out int filled)
    {
        filled = 0;
        int searched = 0;
        while (filled < probe.Length)
        {
            int limit = filled == 0 ? Math.Min(probe.Length, FirstProbeBytes) : probe.Length;
            int read = RandomAccess.Read(handle, probe.AsSpan(filled, limit - filled), from + filled);
            if (read == 0)
            {
                return -1;
            }

            filled += read;
            int index = probe.AsSpan(searched, filled - searched).IndexOf(Newline);
            if (index >= 0)
            {
                return searched + index;
            }

            searched = filled;
        }

        return -1;
    }

    // Local parsing retains this method's run-path diagnostics.
    private static LineDescriptor Describe(byte[] buffer, int offset, int length, long byteOffset, string runPath)
    {
        ReadOnlySpan<byte> line = buffer.AsSpan(offset, length);
        if (!LineParser.TryParse(line, out long number, out int stringStart))
        {
            ReadOnlySpan<byte> preview = line.Length > PreviewMaxBytes ? line[..PreviewMaxBytes] : line;
            throw new MalformedLineException(byteOffset, MalformedLineException.LineNumberUnavailable, Encoding.UTF8.GetString(preview), runPath);
        }

        int stringOffset = offset + stringStart;
        int stringLength = LineDescriptor.StringLengthOf(offset, length, stringOffset);
        ulong prefix = LineDescriptor.BuildPrefix(buffer.AsSpan(stringOffset, stringLength));
        return new LineDescriptor(prefix, number, offset, length, stringOffset);
    }

    private static MalformedLineException Malformed(long byteOffset, byte[] probe, int filled, string runPath)
    {
        ReadOnlySpan<byte> offending = probe.AsSpan(0, Math.Min(filled, PreviewMaxBytes));
        return new MalformedLineException(byteOffset, MalformedLineException.LineNumberUnavailable, Encoding.UTF8.GetString(offending), runPath);
    }

    // The run whose byte range contains position, by binary search over the prefix sums
    // rather than a scan, since the run count reaches into the thousands.
    private static int FindRun(long[] runStarts, long position)
    {
        int low = 0;
        int high = runStarts.Length - 2;
        while (low < high)
        {
            int mid = low + ((high - low + 1) / 2);
            if (runStarts[mid] <= position)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    // One probe buffer per worker preserves the allocation bound. Unwrap exceptions so
    // malformed-line handling receives its original type.
    private static void RunInParallel(
        int count, int probeSize, int maxDegreeOfParallelism, Action<int, byte[]> body, CancellationToken ct)
    {
        try
        {
            Parallel.For(
                0, count,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = maxDegreeOfParallelism },
                () => new byte[probeSize],
                (i, _, probe) =>
                {
                    body(i, probe);
                    return probe;
                },
                static _ => { });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
    }

    // A sampled line with a descriptor over its own array at offset 0.
    private readonly struct SampleLine(byte[] bytes, LineDescriptor descriptor)
    {
        public readonly byte[] Bytes = bytes;
        public readonly LineDescriptor Descriptor = descriptor;
    }
}
