using BenchmarkDotNet.Attributes;
using FileSorter.Merging;
using FileSorter.RunGeneration;
using FileSorter.Startup;

namespace FileSorter.Benchmarks;

// Measures MergeExecutor with 1, 2, 4, or 8 forced merge workers. Every row keeps the
// eight-worker plan's window and fan-in, then lowers only its worker count. GlobalSetup
// asserts each lower worker count stays within the planned memory budget.
// MergeExecutor consumes its run files, so setup rewrites them and creates an executor for
// every iteration. Each iteration has one invocation.
[MemoryDiagnoser]
[SimpleJob(launchCount: 2, warmupCount: 5, iterationCount: 20, invocationCount: 1)]
public class PartitionedMergeBenchmarks
{
    // Matches MergeBenchmarks input generation; only the merge plan varies.
    private const long InputSizeBytes = 128L * 1024 * 1024;
    private const long GenerationMemoryBudgetBytes = 8L * 1024 * 1024;
    private const int MaxLineLength = 4096;
    private const int AssumedMeanLineLength = 32;
    private const int Seed = 20260906;
    private const int GenerationParallelism = 4;

    // Sized so MemoryBudget.Calculate selects eight merge workers; GlobalSetup verifies it.
    private const long MergePlanBudgetBytes = 256L * 1024 * 1024;
    private const int MergePlanParallelism = 8;

    private MemoryPlan _generationPlan;
    private MemoryPlan _mergePlanAt8;
    private string _tempRoot = string.Empty;
    private string _runTemplateRoot = string.Empty;

    // Rewritten for every iteration because MergeExecutor consumes its inputs.
    private byte[][] _runTemplateBytes = [];

    private TemporaryRunSet? _iterationRuns;
    private IReadOnlyList<string> _iterationRunPaths = [];
    private string _iterationOutputPath = string.Empty;

    [Params(1, 2, 4, 8)]
    public int MergeWorkers { get; set; }

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "FileSorter.Benchmarks.PartitionedMerge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _runTemplateRoot = Path.Combine(_tempRoot, "template");
        Directory.CreateDirectory(_runTemplateRoot);

        _generationPlan = MemoryBudget.Calculate(
            GenerationMemoryBudgetBytes, GenerationParallelism, MaxLineLength, AssumedMeanLineLength);

        _mergePlanAt8 = MemoryBudget.Calculate(
            MergePlanBudgetBytes, MergePlanParallelism, MaxLineLength, AssumedMeanLineLength);

        if (_mergePlanAt8.MergeParallelism != MergePlanParallelism)
        {
            throw new InvalidOperationException(
                $"Expected MemoryBudget.Calculate({MergePlanBudgetBytes}, {MergePlanParallelism}, {MaxLineLength}, " +
                $"{AssumedMeanLineLength}) to give MergeParallelism {MergePlanParallelism}, but got " +
                $"{_mergePlanAt8.MergeParallelism}. MergePlanBudgetBytes needs raising (see this class's own comment " +
                "for the arithmetic).");
        }

        // Verify every lower worker count remains within the eight-worker budget.
        foreach (int workers in (int[])[1, 2, 4, 8])
        {
            MemoryPlan forced = _mergePlanAt8 with { MergeParallelism = workers };
            if (forced.WorstCasePhaseTwoBytes > MergePlanBudgetBytes)
            {
                throw new InvalidOperationException(
                    $"Forcing MergeParallelism to {workers} on the plan built for {MergePlanParallelism} workers " +
                    $"exceeds the budget ({forced.WorstCasePhaseTwoBytes} > {MergePlanBudgetBytes} bytes), which the " +
                    "class doc comment's monotonicity argument says cannot happen.");
            }
        }

        byte[] data = SyntheticInput.Generate(InputSizeBytes, Seed);

        // Generate equivalent real run files outside timing.
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
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    // Rebuild consumed run files synchronously because BenchmarkDotNet setup is synchronous.
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
        // Dispose cleans any inputs left by a failed iteration.
        _iterationRuns?.Dispose();

        if (File.Exists(_iterationOutputPath))
        {
            File.Delete(_iterationOutputPath);
        }
    }

    // Uses the production MergeExecutor path, including range partitioning.
    [Benchmark]
    public async Task Partitioned()
    {
        MemoryPlan plan = _mergePlanAt8 with { MergeParallelism = MergeWorkers };
        MergeExecutor executor = new(_iterationRuns!, plan, MaxLineLength);
        await executor.ExecuteAsync(_iterationRunPaths, _iterationOutputPath, CancellationToken.None);
    }
}
