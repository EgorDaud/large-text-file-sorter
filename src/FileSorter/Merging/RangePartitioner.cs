using System.Numerics;
using System.Runtime.ExceptionServices;
using FileSorter.LineFormat;
using Microsoft.Win32.SafeHandles;

namespace FileSorter.Merging;

// Each boundary is the first line at or above its splitter, so equal lines never straddle two workers.
internal static class RangePartitioner
{
    private const byte Newline = (byte)'\n';

    private const int FirstProbeBytes = 8 * 1024;

    // Must be a power of two for AdmitWithinBudget's bit-reversed order.
    internal const int TargetSampleLines = 4096;
    internal const int SampleLineByteBudget = 16 * 1024 * 1024;

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

        int probeSize = Math.Max(FirstProbeBytes, maxLineLength + LineCursor.WindowSlack);
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

    private static SampleLine[] ChooseSplitters(
        IReadOnlyList<string> runPaths, long[] lengths, long[] runStarts, long totalBytes,
        int workerCount, int maxLineLength, int probeSize, int parallelism, CancellationToken ct)
    {
        int sampleCount = TargetSampleLines;
        long retainedBytes = 0;

        SampleLine?[] drawn = new SampleLine?[sampleCount];
        int[] drawRuns = new int[sampleCount];
        long[] drawStarts = new long[sampleCount];
        int[] drawLengths = new int[sampleCount];
        RunInParallel(sampleCount, probeSize, parallelism, (i, probe) =>
        {
            long globalPosition = ((2 * (long)i + 1) * totalBytes) / (2L * sampleCount);
            int run = FindRun(runStarts, globalPosition);
            long position = globalPosition - runStarts[run];

            using SafeFileHandle handle = File.OpenHandle(
                runPaths[run], FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);

            long lineStart = AlignAtOrAfter(handle, lengths[run], position, probe, runPaths[run]);
            if (lineStart >= lengths[run])
            {
                return;
            }

            int length = ReadLineAt(handle, lineStart, probe, maxLineLength, runPaths[run]);
            drawRuns[i] = run;
            drawStarts[i] = lineStart;
            drawLengths[i] = length;

            // Parse every draw, not just retained ones, so a malformed line always surfaces.
            LineDescriptor descriptor = Describe(probe, 0, length, lineStart, runPaths[run]);

            if (Interlocked.Add(ref retainedBytes, length) <= SampleLineByteBudget)
            {
                drawn[i] = new SampleLine(probe.AsSpan(0, length).ToArray(), descriptor);
            }
        }, ct);

        // Over budget, which draws were kept depended on scheduling; re-admit by length so the partition is deterministic.
        if (retainedBytes > SampleLineByteBudget)
        {
            bool[] admitted = AdmitWithinBudget(drawLengths);
            for (int i = 0; i < sampleCount; i++)
            {
                if (!admitted[i])
                {
                    drawn[i] = null;
                }
            }

            RunInParallel(sampleCount, probeSize, parallelism, (i, probe) =>
            {
                if (!admitted[i] || drawn[i] is not null)
                {
                    return;
                }

                string path = runPaths[drawRuns[i]];
                using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);
                int length = ReadLineAt(handle, drawStarts[i], probe, maxLineLength, path);
                byte[] bytes = probe.AsSpan(0, length).ToArray();
                drawn[i] = new SampleLine(bytes, Describe(bytes, 0, length, drawStarts[i], path));
            }, ct);
        }

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

    // Bit-reversed order spreads any budget-truncated prefix evenly across all runs, not just the first.
    private static bool[] AdmitWithinBudget(int[] drawLengths)
    {
        int bits = BitOperations.Log2((uint)drawLengths.Length);
        bool[] admitted = new bool[drawLengths.Length];
        long total = 0;
        for (int j = 0; j < drawLengths.Length; j++)
        {
            int i = ReverseBits(j, bits);
            int length = drawLengths[i];
            if (length > 0 && total + length <= SampleLineByteBudget)
            {
                admitted[i] = true;
                total += length;
            }
        }

        return admitted;
    }

    private static int ReverseBits(int value, int bits)
    {
        int reversed = 0;
        for (int b = 0; b < bits; b++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }

        return reversed;
    }

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

        return AlignAtOrAfter(handle, length, low, probe, runPath);
    }

    private static bool AtOrAbove(
        SafeFileHandle handle, long length, long position, in SampleLine splitter,
        byte[] probe, int maxLineLength, string runPath)
    {
        long lineStart = AlignAtOrAfter(handle, length, position, probe, runPath);
        if (lineStart >= length)
        {
            return true;
        }

        int lineLength = ReadLineAt(handle, lineStart, probe, maxLineLength, runPath);
        LineDescriptor descriptor = Describe(probe, 0, lineLength, lineStart, runPath);

        return LineOrder.Compare(in descriptor, probe, in splitter.Descriptor, splitter.Bytes) >= 0;
    }

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

        throw Malformed(scan, probe, filled, runPath);
    }

    // Don't strip CR: runs are LF-terminated, so a CR is content and must match RunCursor's key.
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

    private static LineDescriptor Describe(byte[] buffer, int offset, int length, long byteOffset, string runPath)
    {
        if (!LineDescriptor.TryCreate(buffer, offset, length, out LineDescriptor descriptor))
        {
            throw new MalformedLineException(
                byteOffset, MalformedLineException.LineNumberUnavailable, MalformedLineException.PreviewOf(buffer.AsSpan(offset, length)), runPath);
        }

        return descriptor;
    }

    private static MalformedLineException Malformed(long byteOffset, byte[] probe, int filled, string runPath) =>
        new(byteOffset, MalformedLineException.LineNumberUnavailable, MalformedLineException.PreviewOf(probe.AsSpan(0, filled)), runPath);

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

    private readonly struct SampleLine(byte[] bytes, LineDescriptor descriptor)
    {
        public readonly byte[] Bytes = bytes;
        public readonly LineDescriptor Descriptor = descriptor;
    }
}
