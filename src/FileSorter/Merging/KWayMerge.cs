using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FileSorter.LineFormat;

namespace FileSorter.Merging;

internal static class KWayMerge
{
    // Takes ownership of every run stream and disposes it on success or failure.
    public static async Task MergeAsync(
        IReadOnlyList<Stream> runs,
        IReadOnlyList<RunCursorBuffers> cursorBuffers,
        Stream output,
        byte[] outputStagingBuffer,
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
            ArgumentOutOfRangeException.ThrowIfLessThan(cursorBuffers.Count, count, nameof(cursorBuffers));

            for (int i = 0; i < count; i++)
            {
                cursors[i] = new RunCursor(runs[i], cursorBuffers[i], maxLineLength, runIndex: i);
            }

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

            LineStager stager = CreateOutputStager(outputStagingBuffer, output, progress, ct);

            // Copy the winning line before advancing its cursor, so its buffer remains stable.
            while (tree.WinnerIsAlive)
            {
                int run = tree.Winner;
                RunCursor runCursor = cursors[run]!;
                LineDescriptor winner = runCursor.Current;
                byte[] source = runCursor.Buffer;
                if (!stager.TryAdd(source.AsSpan(winner.Offset, winner.Length)))
                {
                    await stager.AddAfterFlushAsync(source, winner.Offset, winner.Length);
                }

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

            await stager.FlushAsync();
        }
        catch
        {
            await DisposeAllAsync(cursors, runs, count);
            throw;
        }

        Exception? disposalFailure = await DisposeAllAsync(cursors, runs, count);
        if (disposalFailure is not null)
        {
            ExceptionDispatchInfo.Capture(disposalFailure).Throw();
        }
    }

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

    // Kept out of MergeAsync so the lambda's closure doesn't capture the per-line loop's locals.
    private static LineStager CreateOutputStager(
        byte[] buffer, Stream output, MergeProgress progress, CancellationToken ct) =>
        new(buffer, data => TimedWriteAsync(output, data, progress, ct));

    private static async ValueTask TimedWriteAsync(
        Stream output, ReadOnlyMemory<byte> data, MergeProgress progress, CancellationToken ct)
    {
        long start = Stopwatch.GetTimestamp();
        await output.WriteAsync(data, ct);
        progress.AddOutputWaitTicks(Stopwatch.GetTimestamp() - start);
        progress.AddBytesWritten(data.Length);
    }

    private static int Compare(in RunHead a, in RunHead b)
    {
        Debug.Assert(a.IsAlive && b.IsAlive);
        return LineOrder.Compare(in a.Descriptor, a.Buffer, in b.Descriptor, b.Buffer);
    }

    // Knuth loser tree: _tree[0] is the winner and internal nodes hold losers.
    private readonly struct LoserTree
    {
        private readonly RunHead[] _heads;
        private readonly int[] _tree;

        public LoserTree(int count)
        {
            _heads = new RunHead[count];
            _tree = new int[count];
        }

        public int Winner => _tree[0];
        public bool WinnerIsAlive => _heads[_tree[0]].IsAlive;

        public void SetHead(int run, RunHead head) => _heads[run] = head;
        public void MarkDead(int run) => _heads[run] = default;

        public void Build()
        {
            Array.Fill(_tree, -1);
            for (int leaf = 0; leaf < _heads.Length; leaf++)
            {
                Replay(leaf);
            }
        }

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
