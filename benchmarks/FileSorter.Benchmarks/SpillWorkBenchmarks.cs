using Akka.Actor;
using Akka.Configuration;
using Akka.Streams;
using BenchmarkDotNet.Attributes;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using FileSorter.Startup;

namespace FileSorter.Benchmarks;

// Separates spill sorting from writing to identify which phase differs by strategy.
[MemoryDiagnoser]
// One invocation per iteration because setup rebuilds consumed state.
[SimpleJob(launchCount: 2, warmupCount: 5, iterationCount: 20, invocationCount: 1)]
public class SpillWorkBenchmarks
{
    private const long InputSizeBytes = 8L * 1024 * 1024;
    private const long MemoryBudgetBytes = 2L * 1024 * 1024;
    private const int MaxLineLength = 4096;
    private const int AssumedMeanLineLength = 32;
    private const int Seed = 12345;

    private static readonly byte[] LineTerminator = [(byte)'\n'];

    private byte[] _data = [];
    private string _tempRoot = string.Empty;
    private MemoryPlan _plan;
    private ActorSystem? _system;
    private IMaterializer? _materializer;

    private MemoryStream? _stream;
    private ChunkReader? _reader;
    private TemporaryRunSet? _runs;
    private ChunkSpill? _spill;

    [Params(1, 4)]
    public int Parallelism { get; set; }

    // Sort releases the chunk; write persists it unsorted.
    [Params("sort", "write")]
    public string Work { get; set; } = "sort";

    [GlobalSetup]
    public void GlobalSetup()
    {
        _data = SyntheticInput.Generate(InputSizeBytes, Seed);
        _tempRoot = Path.Combine(Path.GetTempPath(), "FileSorter.Benchmarks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _plan = MemoryBudget.Calculate(MemoryBudgetBytes, Parallelism, MaxLineLength, AssumedMeanLineLength);

        Config quiet = ConfigurationFactory.ParseString("akka.loglevel = OFF\nakka.stdout-loglevel = OFF");
        _system = ActorSystem.Create("sorter-benchmark-spill-work", quiet);
        _materializer = _system.Materializer();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _system?.Dispose();

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _stream = new MemoryStream(_data, writable: false);
        BufferPool pool = new(_plan.ChunkSize, _plan.DescriptorCapacity, _plan.PoolCapacity);
        _reader = new ChunkReader(_stream, pool, MaxLineLength);
        _runs = new TemporaryRunSet(_tempRoot);
        _spill = Work == "sort" ? SortOnlySpillAsync : WriteOnlySpillAsync;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // BenchmarkDotNet requires synchronous iteration cleanup.
        _reader?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _runs?.Dispose();
        _stream?.Dispose();
    }

    // Sort and release the chunk without writing it.
    private static Task<string> SortOnlySpillAsync(Chunk chunk, CancellationToken ct)
    {
        try
        {
            ChunkSorter.Sort(chunk.Buffer.Lines.AsSpan(0, chunk.Count), chunk.Buffer.Bytes);
            return Task.FromResult(string.Empty);
        }
        finally
        {
            chunk.Buffer.Dispose();
        }
    }

    // Write input-order lines without sorting.
    private async Task<string> WriteOnlySpillAsync(Chunk chunk, CancellationToken ct)
    {
        try
        {
            byte[] buffer = chunk.Buffer.Bytes;
            LineDescriptor[] lines = chunk.Buffer.Lines;
            string path = _runs!.CreateRunPath();

            await using FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None, _plan.SpillBufferSize);
            for (int i = 0; i < chunk.Count; i++)
            {
                LineDescriptor line = lines[i];
                await file.WriteAsync(buffer.AsMemory(line.Offset, line.Length), ct);
                await file.WriteAsync(LineTerminator, ct);
            }

            return path;
        }
        finally
        {
            chunk.Buffer.Dispose();
        }
    }

    [Benchmark(Baseline = true)]
    public Task<IReadOnlyList<string>> Channels() =>
        ChannelRunGeneration.RunAsync(_reader!, _spill!, Parallelism, CancellationToken.None);

    [Benchmark]
    public Task<IReadOnlyList<string>> Akka() =>
        AkkaRunGeneration.RunAsync(_reader!, _spill!, Parallelism, _materializer!, CancellationToken.None);
}
