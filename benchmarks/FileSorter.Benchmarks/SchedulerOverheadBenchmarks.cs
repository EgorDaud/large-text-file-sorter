using Akka.Actor;
using Akka.Configuration;
using Akka.Streams;
using BenchmarkDotNet.Attributes;
using FileSorter.RunGeneration;
using FileSorter.Startup;

namespace FileSorter.Benchmarks;

// Isolates reading, scheduling, and buffer hand-off. The no-op spill releases each pool
// slot, matching a real spiller's ownership contract without sorting or writing.
[MemoryDiagnoser]
// One invocation per iteration because setup rebuilds consumed state.
[SimpleJob(launchCount: 2, warmupCount: 5, iterationCount: 20, invocationCount: 1)]
public class SchedulerOverheadBenchmarks
{
    private const long InputSizeBytes = 8L * 1024 * 1024;
    private const long MemoryBudgetBytes = 2L * 1024 * 1024;
    private const int MaxLineLength = 4096;
    private const int AssumedMeanLineLength = 32;
    private const int Seed = 12345;

    private byte[] _data = [];
    private MemoryPlan _plan;
    private ActorSystem? _system;
    private IMaterializer? _materializer;

    private MemoryStream? _stream;
    private ChunkReader? _reader;

    [Params(1, 4)]
    public int Parallelism { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _data = SyntheticInput.Generate(InputSizeBytes, Seed);
        _plan = MemoryBudget.Calculate(MemoryBudgetBytes, Parallelism, MaxLineLength, AssumedMeanLineLength);

        Config quiet = ConfigurationFactory.ParseString("akka.loglevel = OFF\nakka.stdout-loglevel = OFF");
        _system = ActorSystem.Create("sorter-benchmark-overhead", quiet);
        _materializer = _system.Materializer();
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _system?.Dispose();

    [IterationSetup]
    public void IterationSetup()
    {
        _stream = new MemoryStream(_data, writable: false);
        BufferPool pool = new(_plan.ChunkSize, _plan.DescriptorCapacity, _plan.PoolCapacity);
        _reader = new ChunkReader(_stream, pool, MaxLineLength);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // BenchmarkDotNet requires synchronous iteration cleanup.
        _reader?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _stream?.Dispose();
    }

    // Matches the spiller's buffer-release contract. Strategies do not inspect the path.
    private static Task<string> NoOpSpillAsync(Chunk chunk, CancellationToken ct)
    {
        chunk.Buffer.Dispose();
        return Task.FromResult(string.Empty);
    }

    [Benchmark(Baseline = true)]
    public Task<IReadOnlyList<string>> Channels() =>
        ChannelRunGeneration.RunAsync(_reader!, NoOpSpillAsync, Parallelism, CancellationToken.None);

    [Benchmark]
    public Task<IReadOnlyList<string>> Akka() =>
        AkkaRunGeneration.RunAsync(_reader!, NoOpSpillAsync, Parallelism, _materializer!, CancellationToken.None);
}
