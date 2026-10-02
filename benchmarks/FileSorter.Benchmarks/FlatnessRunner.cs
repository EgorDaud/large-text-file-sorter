using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Akka.Actor;
using Akka.Configuration;
using Akka.Streams;
using FileSorter.Cli;
using FileSorter.Infrastructure;
using FileSorter.Merging;
using FileSorter.Planning;
using FileSorter.RunGeneration;

namespace FileSorter.Benchmarks;

// Console modes that measure peak managed heap at a fixed memory budget over several input
// sizes. Each size runs in a fresh worker process because input generation changes GC
// bookkeeping. The parent generates input; the worker measures only the sorting pipeline.
// Every size runs once per run-generation pipeline (Channels and Akka), each in its own worker,
// so a memory regression in either pipeline is caught.
internal static class FlatnessRunner
{
    private static readonly (string Label, long Bytes)[] Sizes =
    [
        ("1 MiB", 1L * 1024 * 1024),
        ("10 MiB", 10L * 1024 * 1024),
        ("100 MiB", 100L * 1024 * 1024),
    ];

    private static readonly (string Label, long Bytes)[] ParallelMergeSizes =
    [
        .. Sizes,
        ("1 GiB", 1L * 1024 * 1024 * 1024),
    ];

    // Fixed budget for the single-worker matrix.
    internal const long MemoryBudgetBytes = 16L * 1024 * 1024;

    // Budget selected to give two merge workers; the worker reports the actual plan.
    internal const long ParallelMergeMemoryBudgetBytes = 64L * 1024 * 1024;

    internal const int MaxLineLength = 4096;
    internal const int AssumedMeanLineLength = 32;
    internal const int Parallelism = 4;
    internal const int SampleIntervalMilliseconds = 5;
    private const int Seed = 424242;

    // Allows GC bookkeeping noise while still flagging large growth.
    private const double TolerancePercent = 60.0;

    public static async Task<int> RunAsync() =>
        await RunMatrixAsync("fixed 16 MiB budget (MergeParallelism 1)", MemoryBudgetBytes, Sizes);

    public static async Task<int> RunParallelMergeAsync() =>
        await RunMatrixAsync(
            "fixed 64 MiB budget (MergeParallelism 2, see ParallelMergeMemoryBudgetBytes)",
            ParallelMergeMemoryBudgetBytes,
            ParallelMergeSizes);

    private static async Task<int> RunMatrixAsync(
        string description, long memoryBudgetBytes, (string Label, long Bytes)[] sizes)
    {
        string tempRoot = ScratchDirectory.Create("FileSorter.Benchmarks.Flatness");

        Pipeline[] pipelines = [Pipeline.Channels, Pipeline.Akka];
        List<(Pipeline Pipeline, string Label, long InputBytes, long PeakBytes, int MergeParallelism)> results = [];
        try
        {
            foreach ((string label, long bytes) in sizes)
            {
                // One input per size, shared by both pipelines; each pipeline still gets its own process.
                string inputPath = await WriteInputAsync(tempRoot, label, bytes);
                foreach (Pipeline pipeline in pipelines)
                {
                    Console.WriteLine($"Sorting {label} ({pipeline})...");
                    (long peak, int mergeParallelism) = await RunInChildProcessAsync(
                        tempRoot, inputPath, label, memoryBudgetBytes, pipeline);
                    results.Add((pipeline, label, bytes, peak, mergeParallelism));
                    Console.WriteLine($"  peak managed heap: {peak / (1024.0 * 1024.0):F2} MiB (MergeParallelism {mergeParallelism})");
                }

                File.Delete(inputPath);
            }
        }
        finally
        {
            ScratchDirectory.Delete(tempRoot);
        }

        // Each matrix must report the same merge parallelism for every size and pipeline.
        int matrixMergeParallelism = results[0].MergeParallelism;
        bool consistentMergeParallelism = results.All(r => r.MergeParallelism == matrixMergeParallelism);

        Console.WriteLine();
        Console.WriteLine($"Flatness result -- {description}");
        Console.WriteLine($"  fixed budget:            {memoryBudgetBytes / (1024.0 * 1024.0):F0} MiB");
        Console.WriteLine($"  merge parallelism (N):   {matrixMergeParallelism}{(consistentMergeParallelism ? string.Empty : " (INCONSISTENT ACROSS SIZES)")}");
        Console.WriteLine("  isolation:               each (size, pipeline) measured in its own freshly launched process (see the type doc comment for why)");
        Console.WriteLine($"  sampling method:         GC.GetTotalMemory(false) in the worker process, background timer, {SampleIntervalMilliseconds} ms interval, running maximum kept (not post-run)");
        Console.WriteLine($"  stated tolerance:        {TolerancePercent:F0}% (largest peak over smallest, per pipeline)");
        Console.WriteLine();
        Console.WriteLine("  Pipeline | Input size | Peak managed heap | Peak vs budget");
        Console.WriteLine("  ---------|------------|--------------------|----------------");
        foreach (Pipeline pipeline in pipelines)
        {
            foreach ((Pipeline _, string label, long inputBytes, long peakBytes, int _) in results.Where(r => r.Pipeline == pipeline))
            {
                double peakMiB = peakBytes / (1024.0 * 1024.0);
                double budgetMiB = memoryBudgetBytes / (1024.0 * 1024.0);
                double vsBudgetPercent = (peakMiB - budgetMiB) * 100.0 / budgetMiB;
                Console.WriteLine(
                    $"  {pipeline,-8} | {label,10} | {peakMiB,8:F2} MiB ({inputBytes / (1024.0 * 1024.0):F0} MiB input) | {vsBudgetPercent,6:F1}%");
            }
        }

        Console.WriteLine();
        bool flat = true;
        foreach (Pipeline pipeline in pipelines)
        {
            long minPeak = results.Where(r => r.Pipeline == pipeline).Min(r => r.PeakBytes);
            long maxPeak = results.Where(r => r.Pipeline == pipeline).Max(r => r.PeakBytes);
            double ratioPercent = minPeak == 0 ? 0 : (maxPeak - minPeak) * 100.0 / minPeak;
            bool pipelineFlat = ratioPercent <= TolerancePercent;
            flat &= pipelineFlat;
            Console.WriteLine($"  observed spread ({pipeline}): {ratioPercent:F1}% ({(pipelineFlat ? "within" : "EXCEEDS")} the stated {TolerancePercent:F0}% tolerance)");
        }

        return flat && consistentMergeParallelism ? 0 : 1;
    }

    // Generate input in the parent so worker sampling excludes generation churn.
    private static async Task<string> WriteInputAsync(string tempRoot, string label, long targetBytes)
    {
        string inputPath = Path.Combine(tempRoot, $"input-{label.Replace(' ', '_')}.txt");
        byte[] data = SyntheticInput.Generate(targetBytes, Seed);
        await File.WriteAllBytesAsync(inputPath, data);
        return inputPath;
    }

    private static async Task<(long PeakBytes, int MergeParallelism)> RunInChildProcessAsync(
        string tempRoot, string inputPath, string label, long memoryBudgetBytes, Pipeline pipeline)
    {
        string safeLabel = $"{label.Replace(' ', '_')}-{pipeline}";
        string outputPath = Path.Combine(tempRoot, $"output-{safeLabel}.txt");
        string runsDirectory = Path.Combine(tempRoot, $"runs-{safeLabel}");

        ProcessStartInfo startInfo = BuildWorkerStartInfo(inputPath, outputPath, runsDirectory, memoryBudgetBytes, pipeline);
        using Process worker = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the flatness worker process.");

        // Drain both pipes concurrently to avoid blocking the child on a full error pipe.
        Task<string> stdoutTask = worker.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = worker.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        await worker.WaitForExitAsync();

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (worker.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The flatness worker process for {label} ({pipeline}) exited with code {worker.ExitCode}. stderr:\n{stderr}");
        }

        if (!long.TryParse(stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long peakBytes))
        {
            throw new InvalidOperationException($"The flatness worker process for {label} printed unparseable output: '{stdout}'.");
        }

        // Stderr carries the plan diagnostic; stdout remains a single peak value.
        int mergeParallelism = ParseMergeParallelism(stderr, label);

        return (peakBytes, mergeParallelism);
    }

    private static int ParseMergeParallelism(string stderr, string label)
    {
        const string Prefix = "MergeParallelism=";
        foreach (string line in stderr.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(Prefix, StringComparison.Ordinal)
                && int.TryParse(trimmed.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
        }

        throw new InvalidOperationException(
            $"The flatness worker process for {label} did not print its '{Prefix}' diagnostic on stderr. stderr:\n{stderr}");
    }

    // Relaunches the current executable or its managed assembly under dotnet.
    private static ProcessStartInfo BuildWorkerStartInfo(
        string inputPath, string outputPath, string runsDirectory, long memoryBudgetBytes, Pipeline pipeline)
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the current process path to relaunch as a worker.");

        ProcessStartInfo startInfo = new(processPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        startInfo.ArgumentList.Add("--flatness-worker");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add(runsDirectory);
        startInfo.ArgumentList.Add(memoryBudgetBytes.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(pipeline.ToString());
        return startInfo;
    }

    // Worker protocol: one peak value on stdout and merge parallelism on stderr.
    public static async Task<int> RunWorkerAsync(
        string inputPath, string outputPath, string runsDirectory, long memoryBudgetBytes, Pipeline pipeline)
    {
        // Exclude startup allocations from the sampled peak.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using PeakMemorySampler sampler = new(SampleIntervalMilliseconds);
        using TemporaryRunSet runs = new(runsDirectory);

        MemoryPlan plan = MemoryBudget.Calculate(memoryBudgetBytes, Parallelism, MaxLineLength, AssumedMeanLineLength);
        Console.Error.WriteLine($"MergeParallelism={plan.MergeParallelism}");

        // Mirrors the sorter's strategy selection in Program.cs: the ActorSystem exists only for Akka and
        // lives as long as the sort. Its startup cost is inside the sampled window.
        using ActorSystem? system = pipeline is Pipeline.Akka ? CreateQuietActorSystem() : null;
        RunGenerationStrategy strategy = system is null
            ? ChannelRunGeneration.RunAsync
            : CreateAkkaStrategy(system.Materializer());

        IReadOnlyList<string> runPaths = await GenerateRunsAsync(inputPath, plan, runs, strategy);

        if (runPaths.Count == 0)
        {
            using (File.Create(outputPath))
            {
            }
        }
        else if (runPaths.Count == 1)
        {
            RunPlacement.Place(runPaths[0], outputPath, runs);
        }
        else
        {
            // Reclaim phase-one allocations before merge buffers are allocated.
            GC.Collect();

            MergeExecutor executor = new(runs, plan, MaxLineLength);
            await executor.ExecuteAsync(runPaths, outputPath, CancellationToken.None);
        }

        Console.WriteLine(sampler.PeakBytes.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    // Keep phase-one objects scoped so they can be collected before merging.
    private static async Task<IReadOnlyList<string>> GenerateRunsAsync(
        string inputPath, MemoryPlan plan, TemporaryRunSet runs, RunGenerationStrategy strategy)
    {
        // Match the production asynchronous input configuration.
        using FileStream input = new(
            inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: FileStreams.Unbuffered,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        BufferPool pool = new(plan.ChunkSize, plan.DescriptorCapacity, plan.PoolCapacity);

        // Dispose the reader before its input stream.
        await using ChunkReader reader = new(input, pool, MaxLineLength);
        ChunkSpiller spiller = new(runs, plan.SpillBufferSize);

        return await strategy(reader, spiller.SpillAsync, Parallelism, CancellationToken.None);
    }

    // The sorter builds this inline in Program.Main, so it is repeated here rather than extracted from src/.
    private static ActorSystem CreateQuietActorSystem() =>
        ActorSystem.Create(
            "flatness",
            ConfigurationFactory.ParseString("akka.loglevel = OFF\nakka.stdout-loglevel = OFF"));

    private static RunGenerationStrategy CreateAkkaStrategy(IMaterializer materializer) =>
        (reader, spill, parallelism, token) => AkkaRunGeneration.RunAsync(reader, spill, parallelism, materializer, token);
}
