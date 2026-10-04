using BenchmarkDotNet.Attributes;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;

namespace FileSorter.Benchmarks;

/// Measures ChunkSorter.Sort alone; the chunk is read and parsed once in GlobalSetup.
[MemoryDiagnoser]
// invocationCount: 1 because IterationSetup runs per iteration, so a second invocation would sort sorted input.
[SimpleJob(launchCount: 2, warmupCount: 5, iterationCount: 20, invocationCount: 1)]
public class ChunkSortBenchmarks
{
    private const int MaxLineLength = 4096;
    private const int Seed = 20260907;

    /// 110, 166 and 221 MiB are roughly the chunk sizes of the 4, 6 and 8 GiB budgets.
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

        // Sized so the whole input is one chunk: fills land past the reader's MaxLineLength + 2 reserve,
        // and the reader prefetches a second slot before parsing the first, hence capacity 2.
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

        // Not released to the pool: every measured Sort reads this buffer.
        _workingDescriptors = new LineDescriptor[_lineCount];
    }

    [IterationSetup]
    public void IterationSetup() => _sourceDescriptors.AsSpan().CopyTo(_workingDescriptors);

    /// The same comparison through the BCL introsort with no bucketing pass.
    [Benchmark(Baseline = true)]
    public void PlainIntrosort() =>
        _workingDescriptors.AsSpan(0, _lineCount).Sort(new DescriptorComparer(_buffer));

    [Benchmark]
    public void Sort() => ChunkSorter.Sort(_workingDescriptors.AsSpan(0, _lineCount), _buffer);

    private readonly struct DescriptorComparer(byte[] buffer) : IComparer<LineDescriptor>
    {
        public int Compare(LineDescriptor x, LineDescriptor y) => LineOrder.Compare(in x, buffer, in y, buffer);
    }
}
