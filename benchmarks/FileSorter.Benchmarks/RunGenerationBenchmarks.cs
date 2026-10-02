using Akka.Actor;
using Akka.Streams;
using BenchmarkDotNet.Attributes;
using FileSorter.Infrastructure;
using FileSorter.Planning;
using FileSorter.RunGeneration;

namespace FileSorter.Benchmarks;

// Times phase one only, so merge I/O cannot hide scheduler costs.
[MemoryDiagnoser]
// One invocation per iteration because setup rebuilds consumed state.
[SimpleJob(launchCount: 2, warmupCount: 5, iterationCount: 20, invocationCount: 1)]
public class RunGenerationBenchmarks
{
    private const long InputSizeBytes = 8L * 1024 * 1024;
    private const long MemoryBudgetBytes = 2L * 1024 * 1024;
    private const int MaxLineLength = 4096;

    private const int Seed = 12345;

    private byte[] _data = [];
    private string _tempRoot = string.Empty;
    private MemoryPlan _plan;
    private ActorSystem? _system;
    private IMaterializer? _materializer;

    private MemoryStream? _stream;
    private ChunkReader? _reader;
    private TemporaryRunSet? _runs;
    private ChunkSpiller? _spiller;

    [Params(1, 4)]
    public int Parallelism { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _data = SyntheticInput.Generate(InputSizeBytes, Seed);
        _tempRoot = ScratchDirectory.Create("FileSorter.Benchmarks");

        _plan = MemoryBudget.Calculate(MemoryBudgetBytes, Parallelism, MaxLineLength, MemoryBudget.AssumedMeanLineLength);

        _system = AkkaRunGeneration.CreateQuietSystem("sorter-benchmark");
        _materializer = _system.Materializer();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _system?.Dispose();

        ScratchDirectory.Delete(_tempRoot);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _stream = new MemoryStream(_data, writable: false);
        BufferPool pool = new(_plan.ChunkSize, _plan.DescriptorCapacity, _plan.PoolCapacity);
        _reader = new ChunkReader(_stream, pool, MaxLineLength);
        _runs = new TemporaryRunSet(_tempRoot);
        _spiller = new ChunkSpiller(_runs, _plan.SpillBufferSize);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // BenchmarkDotNet requires synchronous iteration cleanup.
        _reader?.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _runs?.Dispose();
        _stream?.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task<IReadOnlyList<string>> Channels() =>
        ChannelRunGeneration.RunAsync(_reader!, _spiller!.SpillAsync, Parallelism, CancellationToken.None);

    [Benchmark]
    public Task<IReadOnlyList<string>> Akka() =>
        AkkaRunGeneration.RunAsync(_reader!, _spiller!.SpillAsync, Parallelism, _materializer!, CancellationToken.None);
}
