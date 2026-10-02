using BenchmarkDotNet.Attributes;
using FileSorter.Infrastructure;
using FileSorter.Merging;
using FileSorter.Planning;
using FileSorter.RunGeneration;

namespace FileSorter.Benchmarks;

// Disk inputs are small enough to be page-cached, so Disk measures the stream path, not device throughput.
[MemoryDiagnoser]
// invocationCount: 1 because MergeAsync disposes the streams IterationSetup opens.
[SimpleJob(launchCount: 2, warmupCount: 5, iterationCount: 20, invocationCount: 1)]
public class MergeBenchmarks
{
    private const long InputSizeBytes = 128L * 1024 * 1024;
    private const long MemoryBudgetBytes = 8L * 1024 * 1024;
    private const int MaxLineLength = 4096;

    private const int Seed = 20260906;

    private const int Parallelism = 4;

    private MemoryPlan _plan;
    private string _tempRoot = string.Empty;

    private TemporaryRunSet? _runs;
    private IReadOnlyList<string> _runPaths = [];
    private byte[][] _runBytes = [];

    private RunCursorBuffers[] _cursorBuffers = [];

    private byte[] _outputStagingBuffer = [];

    private Stream[] _diskInputs = [];
    private FileStream? _diskOutput;
    private string? _diskOutputPath;
    private Stream[] _memoryInputs = [];

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        _tempRoot = ScratchDirectory.Create("FileSorter.Benchmarks.Merge");

        _plan = MemoryBudget.Calculate(MemoryBudgetBytes, Parallelism, MaxLineLength, MemoryBudget.AssumedMeanLineLength);

        byte[] data = SyntheticInput.Generate(InputSizeBytes, Seed);

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
            _cursorBuffers[i] = RunCursorBuffers.Create(_plan);
        }

        _outputStagingBuffer = new byte[_plan.OutputBufferSize];
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _runs?.Dispose();

        ScratchDirectory.Delete(_tempRoot);
    }

    [IterationSetup(Target = nameof(Disk))]
    public void DiskIterationSetup()
    {
        _diskInputs = new Stream[_runPaths.Count];
        for (int i = 0; i < _runPaths.Count; i++)
        {
            _diskInputs[i] = new FileStream(
                _runPaths[i], FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: FileStreams.Unbuffered,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        _diskOutputPath = Path.Combine(_tempRoot, $"merge-output-{Guid.NewGuid():N}.tmp");
        _diskOutput = new FileStream(
            _diskOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: FileStreams.Unbuffered,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    [IterationCleanup(Target = nameof(Disk))]
    public void DiskIterationCleanup()
    {
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
        _memoryInputs = [];
    }

    [Benchmark(Baseline = true)]
    public async Task Disk()
    {
        await using (_diskOutput!)
        {
            await KWayMerge.MergeAsync(
                _diskInputs, _cursorBuffers, _diskOutput!, _outputStagingBuffer, MaxLineLength, ct: CancellationToken.None);
        }
    }

    [Benchmark]
    public async Task InMemory()
    {
        await KWayMerge.MergeAsync(
            _memoryInputs, _cursorBuffers, Stream.Null, _outputStagingBuffer, MaxLineLength, ct: CancellationToken.None);
    }
}
