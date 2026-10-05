using System.Numerics;
using System.Runtime.ExceptionServices;
using FileSorter.LineFormat;
using Microsoft.Win32.SafeHandles;

namespace FileSorter.Merging;

// Each boundary is the first line at or above its splitter, so equal lines never straddle two workers.
internal sealed class RangePartitioner
{
    private const byte Newline = (byte)'\n';

    private const int FirstProbeBytes = 8 * 1024;

    // Must be a power of two for AdmitWithinBudget's bit-reversed order.
    public const int TargetSampleLines = 4096;
    public const int SampleLineByteBudget = 16 * 1024 * 1024;

    private readonly IReadOnlyList<string> _runPaths;
    private readonly long[] _lengths;
    private readonly long[] _runStarts;
    private readonly int _maxLineLength;
    private readonly int _probeSize;
    private readonly int _parallelism;
    private readonly CancellationToken _ct;

    private RangePartitioner(
        IReadOnlyList<string> runPaths, long[] lengths, int maxLineLength, int parallelism, CancellationToken ct)
    {
        _runPaths = runPaths;
        _lengths = lengths;
        _runStarts = new long[lengths.Length + 1];
        for (int r = 0; r < lengths.Length; r++)
        {
            _runStarts[r + 1] = _runStarts[r] + lengths[r];
        }

        _maxLineLength = maxLineLength;
        _probeSize = Math.Max(FirstProbeBytes, maxLineLength + LineCursor.WindowSlack);
        _parallelism = parallelism;
        _ct = ct;
    }

    private long TotalBytes => _runStarts[^1];

    public static RangePartition? Locate(
        IReadOnlyList<string> runPaths, int workerCount, int maxLineLength, int parallelism, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 2);

        long[] lengths = [.. runPaths.Select(path => new FileInfo(path).Length)];
        return new RangePartitioner(runPaths, lengths, maxLineLength, parallelism, ct).Partition(workerCount);
    }

    private RangePartition? Partition(int workerCount)
    {
        if (TotalBytes == 0)
        {
            return null;
        }

        SampleLine[] splitters = ChooseSplitters(workerCount);
        if (splitters.Length == 0)
        {
            return null;
        }

        long[][] offsets = new long[_runPaths.Count][];
        RunInParallel(_runPaths.Count, (r, probe) =>
        {
            using RunProbe run = OpenRun(r, probe);

            long[] runOffsets = new long[workerCount + 1];
            runOffsets[workerCount] = run.Length;

            long lower = 0;
            for (int s = 0; s < splitters.Length; s++)
            {
                lower = run.LocateFirstAtOrAbove(lower, in splitters[s]);
                runOffsets[s + 1] = lower;
            }

            offsets[r] = runOffsets;
        });

        return RangePartition.From(offsets, _lengths, workerCount, TotalBytes);
    }

    private SampleLine[] ChooseSplitters(int workerCount)
    {
        SampleLine?[] drawn = new SampleLine?[TargetSampleLines];
        Draw[] draws = new Draw[TargetSampleLines];

        // Over budget, which draws were kept depended on scheduling; re-admit by length so the partition is deterministic.
        if (DrawSamples(drawn, draws) > SampleLineByteBudget)
        {
            ReadmitWithinBudget(drawn, draws);
        }

        List<SampleLine> sample = new(TargetSampleLines);
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

    // Returns the bytes all draws asked to retain, which can exceed the budget.
    private long DrawSamples(SampleLine?[] drawn, Draw[] draws)
    {
        long retainedBytes = 0;
        RunInParallel(draws.Length, (i, probe) =>
        {
            long globalPosition = ((2 * (long)i + 1) * TotalBytes) / (2L * draws.Length);
            int r = FindRun(_runStarts, globalPosition);

            using RunProbe run = OpenRun(r, probe);
            long lineStart = run.AlignAtOrAfter(globalPosition - _runStarts[r]);
            if (lineStart >= run.Length)
            {
                return;
            }

            int length = run.ReadLineAt(lineStart);
            draws[i] = new Draw(r, lineStart, length);

            // Parse every draw, not just retained ones, so a malformed line always surfaces.
            LineDescriptor descriptor = run.Describe(lineStart, length);

            if (Interlocked.Add(ref retainedBytes, length) <= SampleLineByteBudget)
            {
                drawn[i] = new SampleLine(probe.AsSpan(0, length).ToArray(), descriptor);
            }
        });

        return retainedBytes;
    }

    private void ReadmitWithinBudget(SampleLine?[] drawn, Draw[] draws)
    {
        bool[] admitted = AdmitWithinBudget(draws);
        for (int i = 0; i < drawn.Length; i++)
        {
            if (!admitted[i])
            {
                drawn[i] = null;
            }
        }

        RunInParallel(drawn.Length, (i, probe) =>
        {
            if (!admitted[i] || drawn[i] is not null)
            {
                return;
            }

            Draw draw = draws[i];
            using RunProbe run = OpenRun(draw.Run, probe);
            int length = run.ReadLineAt(draw.Start);
            drawn[i] = new SampleLine(probe.AsSpan(0, length).ToArray(), run.Describe(draw.Start, length));
        });
    }

    // Bit-reversed order spreads any budget-truncated prefix evenly across all runs, not just the first.
    private static bool[] AdmitWithinBudget(Draw[] draws)
    {
        int bits = BitOperations.Log2((uint)draws.Length);
        bool[] admitted = new bool[draws.Length];
        long total = 0;
        for (int j = 0; j < draws.Length; j++)
        {
            int i = ReverseBits(j, bits);
            int length = draws[i].Length;
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

    private RunProbe OpenRun(int run, byte[] probe) => new(_runPaths[run], _lengths[run], probe, _maxLineLength);

    private void RunInParallel(int count, Action<int, byte[]> body)
    {
        try
        {
            Parallel.For(
                0, count,
                new ParallelOptions { CancellationToken = _ct, MaxDegreeOfParallelism = _parallelism },
                () => new byte[_probeSize],
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

    private readonly record struct Draw(int Run, long Start, int Length);

    private readonly struct SampleLine(byte[] bytes, LineDescriptor descriptor)
    {
        public readonly byte[] Bytes = bytes;
        public readonly LineDescriptor Descriptor = descriptor;
    }

    // One open run and the worker's probe buffer; every read lands at probe[0].
    private readonly struct RunProbe(string path, long length, byte[] probe, int maxLineLength) : IDisposable
    {
        private readonly SafeFileHandle _handle =
            File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);

        private readonly string _path = path;
        private readonly byte[] _probe = probe;
        private readonly int _maxLineLength = maxLineLength;

        public long Length { get; } = length;

        public long LocateFirstAtOrAbove(long lower, in SampleLine splitter)
        {
            long low = lower;
            long high = Length;
            while (low < high)
            {
                long mid = low + ((high - low) / 2);
                if (AtOrAbove(mid, in splitter))
                {
                    high = mid;
                }
                else
                {
                    low = mid + 1;
                }
            }

            return AlignAtOrAfter(low);
        }

        public long AlignAtOrAfter(long position)
        {
            if (position <= 0)
            {
                return 0;
            }

            long scan = position - 1;
            if (scan >= Length)
            {
                return Length;
            }

            int newline = FillToNewline(scan, out int filled);
            if (newline >= 0)
            {
                return scan + newline + 1;
            }

            if (filled < _probe.Length)
            {
                return Length;
            }

            throw Malformed(scan, filled);
        }

        // Don't strip CR: runs are LF-terminated, so a CR is content and must match RunCursor's key.
        public int ReadLineAt(long lineStart)
        {
            int newline = FillToNewline(lineStart, out int filled);
            int contentLength = newline >= 0 ? newline : filled;

            if (contentLength > _maxLineLength)
            {
                throw Malformed(lineStart, filled);
            }

            return contentLength;
        }

        public LineDescriptor Describe(long lineStart, int length)
        {
            if (!LineDescriptor.TryCreate(_probe, 0, length, out LineDescriptor descriptor))
            {
                throw new MalformedLineException(
                    lineStart, MalformedLineException.LineNumberUnavailable, MalformedLineException.PreviewOf(_probe.AsSpan(0, length)), _path);
            }

            return descriptor;
        }

        public void Dispose() => _handle.Dispose();

        private bool AtOrAbove(long position, in SampleLine splitter)
        {
            long lineStart = AlignAtOrAfter(position);
            if (lineStart >= Length)
            {
                return true;
            }

            int lineLength = ReadLineAt(lineStart);
            LineDescriptor descriptor = Describe(lineStart, lineLength);

            return LineOrder.Compare(in descriptor, _probe, in splitter.Descriptor, splitter.Bytes) >= 0;
        }

        private int FillToNewline(long from, out int filled)
        {
            filled = 0;
            int searched = 0;
            while (filled < _probe.Length)
            {
                int limit = filled == 0 ? Math.Min(_probe.Length, FirstProbeBytes) : _probe.Length;
                int read = RandomAccess.Read(_handle, _probe.AsSpan(filled, limit - filled), from + filled);
                if (read == 0)
                {
                    return -1;
                }

                filled += read;
                int index = _probe.AsSpan(searched, filled - searched).IndexOf(Newline);
                if (index >= 0)
                {
                    return searched + index;
                }

                searched = filled;
            }

            return -1;
        }

        private MalformedLineException Malformed(long byteOffset, int filled) =>
            new(byteOffset, MalformedLineException.LineNumberUnavailable, MalformedLineException.PreviewOf(_probe.AsSpan(0, filled)), _path);
    }
}
