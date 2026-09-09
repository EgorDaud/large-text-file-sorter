using BenchmarkDotNet.Attributes;
using FileSorter.LineFormat;
using FileSorter.Merging;
using FileSorter.RunGeneration;
using FileSorter.Startup;

namespace FileSorter.Benchmarks;

// Measures phase-two merges over run files generated outside timing. Disk uses the
// production stream configuration; InMemory reads the same bytes into Stream.Null.
// The small Disk inputs become page-cached, so they measure the stream path rather than
// device throughput. MemoryStream also completes reads synchronously.
[MemoryDiagnoser]
// One invocation per iteration because setup rebuilds consumed state.
[SimpleJob(launchCount: 2, warmupCount: 5, iterationCount: 20, invocationCount: 1)]
public class MergeBenchmarks
{
    // The plan produces many runs while keeping the benchmark practical.
    private const long InputSizeBytes = 128L * 1024 * 1024;
    private const long MemoryBudgetBytes = 8L * 1024 * 1024;
    private const int MaxLineLength = 4096;
    private const int AssumedMeanLineLength = 32;
    private const int Seed = 20260906;

    // Used only to generate runs in GlobalSetup.
    private const int Parallelism = 4;

    private MemoryPlan _plan;
    private string _tempRoot = string.Empty;

    // Owns run files reused by every iteration until GlobalCleanup.
    private TemporaryRunSet? _runs;
    private IReadOnlyList<string> _runPaths = [];
    private byte[][] _runBytes = [];

    // Reuse production-sized cursor buffers to keep allocation churn outside timing.
    private RunCursorBuffers[] _cursorBuffers = [];

    // Shared by non-overlapping benchmark iterations.
    private byte[] _outputStagingBuffer = [];

    private Stream[] _diskInputs = [];
    private FileStream? _diskOutput;
    private string? _diskOutputPath;
    private Stream[] _memoryInputs = [];

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "FileSorter.Benchmarks.Merge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _plan = MemoryBudget.Calculate(MemoryBudgetBytes, Parallelism, MaxLineLength, AssumedMeanLineLength);

        byte[] data = SyntheticInput.Generate(InputSizeBytes, Seed);

        // Generate real run files outside timing. Scheduler comparisons are separate.
        using MemoryStream input = new(data, writable: false);
        _runs = new TemporaryRunSet(_tempRoot);
        BufferPool pool = new(_plan.ChunkSize, _plan.DescriptorCapacity, _plan.PoolCapacity);
        await using ChunkReader reader = new(input, pool, MaxLineLength);
        ChunkSpiller spiller = new(_runs, _plan.SpillBufferSize);

        _runPaths = await ChannelRunGeneration.RunAsync(reader, spiller.SpillAsync, Parallelism, CancellationToken.None);

        _runBytes = new byte[_runPaths.Count][];
        for (int i = 0; i < _runPaths.Count; i++)
        {
            _runBytes[i] = await File.ReadAllBytesAsync(_runPaths[i]);
        }

        int fanIn = _runPaths.Count;
        _cursorBuffers = new RunCursorBuffers[fanIn];
        for (int i = 0; i < fanIn; i++)
        {
            _cursorBuffers[i] = new RunCursorBuffers(
                new byte[_plan.ReadAheadBufferSize],
                new byte[_plan.ReadAheadBufferSize],
                new LineDescriptor[_plan.ReadAheadDescriptorCapacity]);
        }

        _outputStagingBuffer = new byte[_plan.OutputBufferSize];
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        // Remove tracked runs and any leftover temporary files.
        _runs?.Dispose();

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    // Targeted setup opens streams only for Disk. MergeAsync owns and closes them, so each
    // iteration reopens streams with the production configuration.
    [IterationSetup(Target = nameof(Disk))]
    public void DiskIterationSetup()
    {
        _diskInputs = new Stream[_runPaths.Count];
        for (int i = 0; i < _runPaths.Count; i++)
        {
            _diskInputs[i] = new FileStream(
                _runPaths[i], FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        _diskOutputPath = Path.Combine(_tempRoot, $"merge-output-{Guid.NewGuid():N}.tmp");
        _diskOutput = new FileStream(
            _diskOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    [IterationCleanup(Target = nameof(Disk))]
    public void DiskIterationCleanup()
    {
        // MergeAsync closes streams; delete this iteration's output file.
        if (_diskOutputPath is not null && File.Exists(_diskOutputPath))
        {
            File.Delete(_diskOutputPath);
        }
    }

    [IterationSetup(Target = nameof(InMemory))]
    public void InMemoryIterationSetup()
    {
        _memoryInputs = new Stream[_runBytes.Length];
        for (int i = 0; i < _runBytes.Length; i++)
        {
            _memoryInputs[i] = new MemoryStream(_runBytes[i], writable: false);
        }
    }

    [IterationCleanup(Target = nameof(InMemory))]
    public void InMemoryIterationCleanup()
    {
        // Release the array after MergeAsync closes its streams.
        _memoryInputs = [];
    }

    // Baseline uses the production on-disk stream path.
    [Benchmark(Baseline = true)]
    public async Task Disk()
    {
        await using (_diskOutput!)
        {
            await KWayMerge.MergeAsync(
                _diskInputs, _cursorBuffers, _diskOutput!, _outputStagingBuffer, MaxLineLength, ct: CancellationToken.None);
        }
    }

    // Uses the same runs and fan-in without file I/O.
    [Benchmark]
    public async Task InMemory()
    {
        await KWayMerge.MergeAsync(
            _memoryInputs, _cursorBuffers, Stream.Null, _outputStagingBuffer, MaxLineLength, ct: CancellationToken.None);
    }
}
