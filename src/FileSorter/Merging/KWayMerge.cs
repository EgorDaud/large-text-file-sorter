using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FileSorter.LineFormat;

namespace FileSorter.Merging;

internal static class KWayMerge
{
    private static readonly byte[] LineTerminator = [(byte)'\n'];

    // Takes ownership of every run stream and disposes it on success or failure.
    public static async Task MergeAsync(
        IReadOnlyList<Stream> runs,
        IReadOnlyList<RunCursorBuffers> cursorBuffers,   // One caller-supplied buffer set per run.
        Stream output,
        byte[] outputStagingBuffer,   // Caller-supplied and priced by MemoryPlan.
        int maxLineLength,
        MergeProgress? progress = null,
        CancellationToken ct = default)
    {
        progress ??= new MergeProgress();

        int count = runs.Count;
        if (count == 0)
        {
            return;
        }

        // Construct cursors inside the try so a buffer validation failure still releases every run.
        RunCursor?[] cursors = new RunCursor?[count];

        try
        {
            for (int i = 0; i < count; i++)
            {
                // The caller maps this index to a path and slice for malformed-line errors.
                cursors[i] = new RunCursor(runs[i], cursorBuffers[i], maxLineLength, runIndex: i);
            }

            // Tree metadata is sized to the fan-in and priced once per concurrent worker.
            LoserTree tree = new(count);
            for (int i = 0; i < count; i++)
            {
                RunCursor cursor = cursors[i]!;
                if (await cursor.MoveNextAsync(ct))
                {
                    tree.SetHead(i, new RunHead(cursor.Buffer, cursor.Current));
                }
            }

            tree.Build();

            // Copy the winning line before advancing its cursor, so its buffer remains stable.
            int stagedLength = 0;
            while (tree.WinnerIsAlive)
            {
                int run = tree.Winner;
                RunCursor runCursor = cursors[run]!;
                LineDescriptor winner = runCursor.Current;
                byte[] source = runCursor.Buffer;
                if (!TryStageLine(outputStagingBuffer, ref stagedLength, source.AsSpan(winner.Offset, winner.Length)))
                {
                    stagedLength = await StageLineAsync(
                        output, outputStagingBuffer, stagedLength, source, winner.Offset, winner.Length, progress, ct);
                }

                // Avoid the async state machine while the current window has descriptors.
                if (runCursor.TryMoveNext() || await runCursor.MoveNextAsync(ct))
                {
                    tree.SetHead(run, new RunHead(runCursor.Buffer, runCursor.Current));
                }
                else
                {
                    tree.MarkDead(run);
                }

                tree.Replay(run);
            }

            // Flush the final buffered terminator after all lines are staged.
            if (stagedLength > 0)
            {
                await TimedWriteAsync(output, outputStagingBuffer.AsMemory(0, stagedLength), progress, ct);
            }
        }
        catch
        {
            // Release wrapped and unwrapped runs. Preserve the original merge failure even
            // if cleanup also fails.
            await DisposeAllAsync(cursors, runs, count);
            throw;
        }

        // On success, a disposal failure is the operation failure.
        Exception? disposalFailure = await DisposeAllAsync(cursors, runs, count);
        if (disposalFailure is not null)
        {
            // Preserve the failing disposal's stack.
            ExceptionDispatchInfo.Capture(disposalFailure).Throw();
        }
    }

    // Disposes every constructed cursor and every unwrapped stream. It continues after
    // cleanup errors and returns the first one for the success path.
    private static async Task<Exception?> DisposeAllAsync(RunCursor?[] cursors, IReadOnlyList<Stream> runs, int count)
    {
        List<Exception>? disposalFailures = null;
        for (int i = 0; i < count; i++)
        {
            try
            {
                if (cursors[i] is { } cursor)
                {
                    await cursor.DisposeAsync();
                }
                else
                {
                    await runs[i].DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                (disposalFailures ??= []).Add(ex);
            }
        }

        return disposalFailures?[0];
    }

    // Stages one line and its terminator without flushing. On failure it leaves the buffer
    // unchanged for StageLineAsync to flush.
    private static bool TryStageLine(byte[] staging, ref int stagedLength, ReadOnlySpan<byte> line)
    {
        int needed = line.Length + 1;
        if (stagedLength + needed > staging.Length)
        {
            return false;
        }

        line.CopyTo(staging.AsSpan(stagedLength));
        stagedLength += line.Length;
        staging[stagedLength] = LineTerminator[0];
        stagedLength++;
        return true;
    }

    // Flushes before staging, or writes a line larger than the staging buffer directly.
    private static async ValueTask<int> StageLineAsync(
        Stream output, byte[] staging, int stagedLength, byte[] source, int offset, int length,
        MergeProgress progress, CancellationToken ct)
    {
        int needed = length + 1;
        if (needed > staging.Length)
        {
            if (stagedLength > 0)
            {
                await TimedWriteAsync(output, staging.AsMemory(0, stagedLength), progress, ct);
            }

            await TimedWriteAsync(output, source.AsMemory(offset, length), progress, ct);
            await TimedWriteAsync(output, LineTerminator, progress, ct);
            return 0;
        }

        // TryStageLine already ruled out fitting alongside what was staged before,
        // so flushing here always empties a genuinely non-empty buffer.
        Debug.Assert(stagedLength > 0, "TryStageLine returning false means something was already staged");
        await TimedWriteAsync(output, staging.AsMemory(0, stagedLength), progress, ct);

        source.AsSpan(offset, length).CopyTo(staging);
        staging[length] = LineTerminator[0];
        return length + 1;
    }

    // Measures every output write once for both byte and wait-time progress.
    private static async ValueTask TimedWriteAsync(
        Stream output, ReadOnlyMemory<byte> data, MergeProgress progress, CancellationToken ct)
    {
        long start = Stopwatch.GetTimestamp();
        await output.WriteAsync(data, ct);
        progress.AddOutputWaitTicks(Stopwatch.GetTimestamp() - start);
        progress.AddBytesWritten(data.Length);
    }

    // Refresh a head before replaying it. A head keeps the current buffer stable while the
    // other cursor window prefetches; a null buffer marks an exhausted leaf. Internal
    // visibility lets MemoryPlan and tests price its size.
    internal readonly struct RunHead
    {
        public readonly byte[]? Buffer;
        public readonly LineDescriptor Descriptor;

        public RunHead(byte[] buffer, LineDescriptor descriptor)
        {
            Buffer = buffer;
            Descriptor = descriptor;
        }

        public bool IsAlive => Buffer is not null;
    }

    // Compares live heads only. LoserTree.Wins applies the run-index tie-break.
    private static int Compare(in RunHead a, in RunHead b)
    {
        // A dead head would compare as an empty span, so assert before comparing.
        Debug.Assert(a.IsAlive && b.IsAlive);
        return LineOrder.Compare(in a.Descriptor, a.Buffer, in b.Descriptor, b.Buffer);
    }

    // Knuth loser tree: tree[0] is the winner and internal nodes hold losers. Replaying an
    // advanced leaf visits O(log2 k) nodes rather than performing heap dequeue/enqueue.
    private readonly struct LoserTree
    {
        private readonly RunHead[] _heads;
        private readonly int[] _tree;

        public LoserTree(int count)
        {
            _heads = new RunHead[count];
            _tree = new int[count];
        }

        // Build waits for callers to load each asynchronous first head.

        public int Winner => _tree[0];
        public bool WinnerIsAlive => _heads[_tree[0]].IsAlive;

        public void SetHead(int run, RunHead head) => _heads[run] = head;
        public void MarkDead(int run) => _heads[run] = default;

        // -1 means no leaf has reached an internal node. Dead heads still occupy leaves and
        // lose normally, so Build has no empty-run special case.
        public void Build()
        {
            Array.Fill(_tree, -1);
            for (int leaf = 0; leaf < _heads.Length; leaf++)
            {
                Replay(leaf);
            }
        }

        // Replays one leaf to the root during Build or after that run advances or dies.
        public void Replay(int leaf)
        {
            int candidate = leaf;
            int parent = (leaf + _heads.Length) / 2;
            while (parent > 0)
            {
                int opponent = _tree[parent];
                if (opponent < 0)
                {
                    _tree[parent] = candidate;
                    return;
                }

                if (!Wins(candidate, opponent))
                {
                    (candidate, opponent) = (opponent, candidate);
                }

                _tree[parent] = opponent;
                parent /= 2;
            }

            _tree[0] = candidate;
        }

        // The run index breaks equal live and dead heads, giving the tournament a strict
        // order independent of build or replay order.
        private bool Wins(int a, int b)
        {
            bool aliveA = _heads[a].IsAlive;
            bool aliveB = _heads[b].IsAlive;
            if (aliveA != aliveB)
            {
                return aliveA;
            }

            if (!aliveA)
            {
                return a < b;
            }

            int cmp = Compare(in _heads[a], in _heads[b]);
            return cmp != 0 ? cmp < 0 : a < b;
        }
    }
}
