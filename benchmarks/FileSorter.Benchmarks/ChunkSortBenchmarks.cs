using BenchmarkDotNet.Attributes;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;

namespace FileSorter.Benchmarks;

/// ChunkSorter.Sort in isolation, at chunk sizes realistic for the target
/// budget rather than SpillWorkBenchmarks' 8 MiB input (where 2 MiB chunks are
/// too small for a bucketing pass to show anything -- the whole point of
/// bucketing is keeping a large chunk's working set in cache during the sort).
/// GlobalSetup parses one real chunk per size through a real ChunkReader, once;
/// IterationSetup then only copies the already-parsed descriptor array, so what
/// is measured is the sort alone, not the read or the parse.
///
/// This class understates every change to the sort's key handling, and by how much
/// is not a constant. SyntheticInput's ten-word vocabulary spans ten distinct top
/// bytes against the real generator's 48, and its words are short, so a radix that
/// descends past the first byte finds far less to separate here than it does on a
/// real chunk. The figure that answers "what did this do to a real sort" is the one
/// taken on this project's 20 GiB file, in docs/measurements.md; this class is the
/// repeatable in-process instrument beside it, not a substitute for it.
[MemoryDiagnoser]
// invocationCount: 1, made explicit on every class with an IterationSetup rather
// than relied on: IterationSetup runs once per iteration, not once per invocation,
// so any invocation after the first in an iteration would sort an array the
// previous invocation already sorted -- the cheapest possible input for both the
// bucketing pass and the introsort.
[SimpleJob(launchCount: 2, warmupCount: 5, iterationCount: 20, invocationCount: 1)]
public class ChunkSortBenchmarks
{
    private const int MaxLineLength = 4096;
    private const int Seed = 20260907;

    /// Swept to locate the knee in cost per byte, which is what decides whether a
    /// chunk-size ceiling is worth having: MemoryBudget.Calculate makes ChunkSize
    /// linear in the budget with no cap, so every extra byte of budget is spent on
    /// the one term whose total work grows as M log C. The shipped budgets land at
    /// roughly 110 MiB (4 GiB), 166 MiB (6 GiB) and 221 MiB (8 GiB), so the sweep
    /// brackets all three and runs past them.
    [Params(
        16 * 1024 * 1024,
        32 * 1024 * 1024,
        64 * 1024 * 1024,
        110 * 1024 * 1024,
        166 * 1024 * 1024,
        221 * 1024 * 1024,
        320 * 1024 * 1024)]
    public int ChunkSizeBytes { get; set; }

    private byte[] _buffer = [];
    private LineDescriptor[] _sourceDescriptors = [];
    private int _lineCount;

    private LineDescriptor[] _workingDescriptors = [];

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        byte[] data = SyntheticInput.Generate(ChunkSizeBytes, Seed);

        // A pool sized to hold the whole generated input as one chunk: one
        // descriptor per line actually present, plus one so the reader never hits
        // its descriptor-exhaustion boundary, which is not what this class measures.
        // Sizing from the data rather than from a ratio keeps the 256 MiB row's
        // descriptor array at what the lines need rather than at gigabytes.
        //
        // The buffer needs MaxLineLength + 2 bytes more than the data itself, not
        // ChunkSizeBytes + 1: every fill's fresh bytes now land past Reserve
        // (maxLineLength + 2), a fixed region reserved ahead of the carry it will
        // hold (design-spec 5.2, ChunkReader's fill/parse overlap), so a
        // buffer sized only ChunkSizeBytes + 1 has a fresh-fill region a whole
        // Reserve short of the data and ends this one chunk on a manufactured carry
        // instead of the clean single fill "one chunk" is supposed to mean here.
        //
        // Capacity 2, not 1: the reader now always acquires a second slot and issues
        // its fill before parsing the first (the same overlap again -- that
        // overlap is the whole point, and it has to start before the parse can tell
        // it whether a second chunk will even be needed), so even this single-chunk
        // read needs room for two slots at once. The second is released unused,
        // once, the moment the parse below finds nothing trailing this chunk.
        int descriptorCapacity = data.AsSpan().Count((byte)'\n') + 1;
        BufferPool pool = new(ChunkSizeBytes + MaxLineLength + 2, descriptorCapacity, capacity: 2);

        using MemoryStream stream = new(data, writable: false);
        await using ChunkReader reader = new(stream, pool, MaxLineLength);
        Chunk chunk = (await reader.ReadNextAsync(CancellationToken.None))
            ?? throw new InvalidOperationException("The synthetic input produced no lines to sort.");

        _buffer = chunk.Buffer.Bytes;
        _lineCount = chunk.Count;
        _sourceDescriptors = new LineDescriptor[_lineCount];
        chunk.Buffer.Lines.AsSpan(0, _lineCount).CopyTo(_sourceDescriptors);

        // Deliberately not released back to the pool: every measured Sort reads
        // line bytes out of this buffer, so handing it back would make the sort
        // read memory the pool considers free. The pool is a local that nothing
        // else ever draws from, and it dies with this method.

        _workingDescriptors = new LineDescriptor[_lineCount];
    }

    // The byte buffer is never mutated by ChunkSorter.Sort -- only the descriptor
    // array is permuted -- so only the descriptors need a fresh unsorted copy
    // each iteration; re-parsing or re-copying the bytes would measure an
    // allocation this sort does not do.
    [IterationSetup]
    public void IterationSetup() => _sourceDescriptors.AsSpan().CopyTo(_workingDescriptors);

    [Benchmark]
    public void Sort() => ChunkSorter.Sort(_workingDescriptors.AsSpan(0, _lineCount), _buffer);
}
