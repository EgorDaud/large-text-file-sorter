using System.Diagnostics;
using Akka.Actor;
using Akka.Streams;
using FileSorter.Infrastructure;
using FileSorter.Merging;
using FileSorter.Planning;
using FileSorter.RunGeneration;
using Shared;

namespace FileSorter.Cli;

internal static class SortCommand
{
    public static int Execute(string[] args)
    {
        if (!CommandLine.TryParseOptions(args, out SorterOptions? options, out string? error))
        {
            return CommandLine.ReportUsageError(error);
        }

        return ConsoleRun.Run(ct => RunAsync(options, ct));
    }

    public static async Task<int> RunAsync(SorterOptions options, CancellationToken ct)
    {
        FileInfo inputInfo = Preflight.ValidateReadableFile(options.InputPath, "Input");
        MemoryPlan plan = PlanMemory(options);

        string outputDirectory = Preflight.DirectoryOf(options.OutputPath);
        Preflight.ValidateOutputDirectory(outputDirectory);

        // The temp directory must exist before its capacity is probed.
        using TemporaryRunSet runs = Preflight.CreateTemporaryRunSet(options.TempDirectory);

        VolumeCapacity.Check(inputInfo.Length, options.TempDirectory, outputDirectory);

        Stopwatch clock = Stopwatch.StartNew();

        if (options.Pipeline is Pipeline.Akka)
        {
            using ActorSystem system = AkkaRunGeneration.CreateQuietSystem("sorter");
            await RunPhasesAsync(
                options, inputInfo, plan, runs, AkkaRunGeneration.Strategy(system.Materializer()), clock, ct);
        }
        else
        {
            await RunPhasesAsync(options, inputInfo, plan, runs, ChannelRunGeneration.RunAsync, clock, ct);
        }

        Console.Error.WriteLine($"Sorted {ByteSize.Describe(inputInfo.Length)} in {clock.Elapsed.TotalSeconds:F1}s.");
        return ExitCodes.Success;
    }

    private static async Task RunPhasesAsync(
        SorterOptions options,
        FileInfo inputInfo,
        MemoryPlan plan,
        TemporaryRunSet runs,
        RunGenerationStrategy strategy,
        Stopwatch clock,
        CancellationToken ct)
    {
        IReadOnlyList<string> runPaths =
            await RunGenerationDriver.GenerateRunsAsync(inputInfo, options.MaxLineLength, plan, runs, strategy, clock, ct);

        Console.Error.WriteLine($"  phase one produced {runPaths.Count} run(s) ({clock.Elapsed.TotalSeconds:F1}s)");

        ct.ThrowIfCancellationRequested();

        if (runPaths.Count == 0)
        {
            RunPlacement.PlaceEmpty(options.OutputPath);
        }
        else if (runPaths.Count == 1)
        {
            RunPlacement.Place(runPaths[0], options.OutputPath, runs);
        }
        else
        {
            // Reclaim the phase-one pool (gen-2 large arrays) before phase two allocates from the same budget.
            GC.Collect();

            MergeExecutor executor = new(runs, plan, options.MaxLineLength);
            await ProgressReporter.RunWithProgressAsync(
                progressCt => MergeReporter.ReportProgressAsync(executor, clock, progressCt),
                () => executor.ExecuteAsync(runPaths, options.OutputPath, ct),
                ct);

            MergeReporter.ReportSummary(executor);
        }
    }

    private static MemoryPlan PlanMemory(SorterOptions options)
    {
        int assumedMeanLineLength = MemoryBudget.AssumedMeanFor(options.MaxLineLength);
        if (MemoryBudget.TryCalculate(
                options.MemoryBudgetBytes, options.Parallelism, options.MaxLineLength, assumedMeanLineLength, out MemoryPlan plan))
        {
            return plan;
        }

        if (!MemoryBudget.TryFindMinimumViableBudget(
                options.Parallelism, options.MaxLineLength, assumedMeanLineLength, out long minimum))
        {
            throw new PreflightException(
                $"No --memory budget can support --parallelism {options.Parallelism} with --max-line " +
                $"{ByteSize.Describe(options.MaxLineLength)}; lower one of them.",
                ExitCodes.InvalidArguments);
        }

        throw new PreflightException(
            $"A --memory budget of {ByteSize.Describe(options.MemoryBudgetBytes)} is too small. At --parallelism " +
            $"{options.Parallelism} with --max-line {ByteSize.Describe(options.MaxLineLength)}, the sorter needs at " +
            $"least {ByteSize.Describe(minimum)}.",
            ExitCodes.InvalidArguments);
    }
}
