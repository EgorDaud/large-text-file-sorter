using BenchmarkDotNet.Attributes;
using FileSorter.Infrastructure;
using FileSorter.Merging;
using FileSorter.Planning;
using FileSorter.RunGeneration;

namespace FileSorter.Benchmarks;

// Every row keeps the eight-worker plan's window and fan-in and forces only MergeParallelism down.
[MemoryDiagnoser]
[SimpleJob(launchCount: 2, warmupCount: 5, iterationCount: 20, invocationCount: 1)]
public class PartitionedMergeBenchmarks
{
    private const long InputSizeBytes = 128L * 1024 * 1024;
    private const long GenerationMemoryBudgetBytes = 8L * 1024 * 1024;
    private const int MaxLineLength = 4096;

    private const int Seed = 20260906;
    private const int GenerationParallelism = 4;

    private const long MergePlanBudgetBytes = 256L * 1024 * 1024;
    private const int MergePlanParallelism = 8;

    private MemoryPlan _generationPlan;
    private MemoryPlan _mergePlanAt8;
    private string _tempRoot = string.Empty;
    private string _runTemplateRoot = string.Empty;

    private byte[][] _runTemplateBytes = [];

    private TemporaryRunSet? _iterationRuns;
    private IReadOnlyList<string> _iterationRunPaths = [];
    private string _iterationOutputPath = string.Empty;

    [Params(1, 2, 4, 8)]
    public int MergeWorkers { get; set; }

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        _tempRoot = ScratchDirectory.Create("FileSorter.Benchmarks.PartitionedMerge");
        _runTemplateRoot = Path.Combine(_tempRoot, "template");
        Directory.CreateDirectory(_runTemplateRoot);

        _generationPlan = MemoryBudget.Calculate(
            GenerationMemoryBudgetBytes, GenerationParallelism, MaxLineLength, MemoryBudget.AssumedMeanLineLength);

        _mergePlanAt8 = MemoryBudget.Calculate(
            MergePlanBudgetBytes, MergePlanParallelism, MaxLineLength, MemoryBudget.AssumedMeanLineLength);

        if (_mergePlanAt8.MergeParallelism != MergePlanParallelism)
        {
            throw new InvalidOperationException(
                $"Expected MemoryBudget.Calculate({MergePlanBudgetBytes}, {MergePlanParallelism}, {MaxLineLength}, " +
                $"{MemoryBudget.AssumedMeanLineLength}) to give MergeParallelism {MergePlanParallelism}, but got " +
                $"{_mergePlanAt8.MergeParallelism}. Raise MergePlanBudgetBytes.");
        }

        foreach (int workers in (int[])[1, 2, 4, 8])
        {
            MemoryPlan forced = _mergePlanAt8 with { MergeParallelism = workers };
            if (forced.WorstCasePhaseTwoBytes > MergePlanBudgetBytes)
            {
                throw new InvalidOperationException(
                    $"Forcing MergeParallelism to {workers} on the plan built for {MergePlanParallelism} workers " +
                    $"exceeds the budget ({forced.WorstCasePhaseTwoBytes} > {MergePlanBudgetBytes} bytes); fewer workers " +
                    "must never cost more.");
            }
        }

        byte[] data = SyntheticInput.Generate(InputSizeBytes, Seed);

        using MemoryStream input = new(data, writable: false);
        using TemporaryRunSet templateRuns = new(_runTemplateRoot);
        BufferPool pool = new(_generationPlan.ChunkSize, _generationPlan.DescriptorCapacity, _generationPlan.PoolCapacity);
        await using ChunkReader reader = new(input, pool, MaxLineLength);
        ChunkSpiller spiller = new(templateRuns, _generationPlan.SpillBufferSize);

        IReadOnlyList<string> templatePaths =
            await ChannelRunGeneration.RunAsync(reader, spiller.SpillAsync, GenerationParallelism, CancellationToken.None);

        _runTemplateBytes = new byte[templatePaths.Count][];
        for (int i = 0; i < templatePaths.Count; i++)
        {
            _runTemplateBytes[i] = await File.ReadAllBytesAsync(templatePaths[i]);
        }

        Console.WriteLine($"PartitionedMergeBenchmarks: {_runTemplateBytes.Length} template runs generated.");
        Console.WriteLine(
            $"PartitionedMergeBenchmarks: merge plan at {MergePlanBudgetBytes / (1024.0 * 1024.0):F0} MiB / " +
            $"parallelism {MergePlanParallelism}: ReadAheadBufferSize={_mergePlanAt8.ReadAheadBufferSize}, " +
            $"MergeFanIn={_mergePlanAt8.MergeFanIn}, MergeParallelism={_mergePlanAt8.MergeParallelism} " +
            "(this plan's window and fan-in are shared by every [Params] row -- only MergeParallelism is forced down).");
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        ScratchDirectory.Delete(_tempRoot);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        string iterationDirectory = Path.Combine(_tempRoot, $"runs-{Guid.NewGuid():N}");
        _iterationRuns = new TemporaryRunSet(iterationDirectory);

        string[] paths = new string[_runTemplateBytes.Length];
        for (int i = 0; i < _runTemplateBytes.Length; i++)
        {
            string path = _iterationRuns.CreateRunPath();
            File.WriteAllBytes(path, _runTemplateBytes[i]);
            paths[i] = path;
        }

        _iterationRunPaths = paths;
        _iterationOutputPath = Path.Combine(_tempRoot, $"output-{Guid.NewGuid():N}.tmp");
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _iterationRuns?.Dispose();

        if (File.Exists(_iterationOutputPath))
        {
            File.Delete(_iterationOutputPath);
        }
    }

    [Benchmark]
    public async Task Partitioned()
    {
        MemoryPlan plan = _mergePlanAt8 with { MergeParallelism = MergeWorkers };
        MergeExecutor executor = new(_iterationRuns!, plan, MaxLineLength);
        await executor.ExecuteAsync(_iterationRunPaths, _iterationOutputPath, CancellationToken.None);
    }
}
