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

        // Only the Akka pipeline has a system, and only phase one uses it: it terminates before the merge allocates.
        IReadOnlyList<string> runPaths;
        using (ActorSystem? system = options.Pipeline is Pipeline.Akka ? AkkaRunGeneration.CreateQuietSystem("sorter") : null)
        {
            RunGenerationStrategy strategy = system is null
                ? ChannelRunGeneration.RunAsync
                : AkkaRunGeneration.Strategy(system.Materializer());

            runPaths = await RunGenerationDriver.GenerateRunsAsync(
                inputInfo.FullName, options.MaxLineLength, plan, runs, WithReadProgress(strategy, inputInfo.Length, clock), ct);
        }

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

        Console.Error.WriteLine($"Sorted {ByteSize.Describe(inputInfo.Length)} in {clock.Elapsed.TotalSeconds:F1}s.");
        return ExitCodes.Success;
    }

    // The reader exists only inside the driver, but every strategy receives it, so progress can wrap the strategy.
    private static RunGenerationStrategy WithReadProgress(
        RunGenerationStrategy strategy, long inputBytes, Stopwatch clock) =>
        (reader, spill, parallelism, ct) => ProgressReporter.RunWithProgressAsync(
            progressCt => ReportReadProgressAsync(reader, inputBytes, clock, progressCt),
            () => strategy(reader, spill, parallelism, ct),
            ct);

    // Reads BytesConsumed without locking; aligned long reads are atomic on 64-bit targets.
    private static Task ReportReadProgressAsync(
        ChunkReader reader, long inputBytes, Stopwatch clock, CancellationToken ct)
    {
        string ofTotal = inputBytes > 0 ? $" of {ByteSize.Describe(inputBytes)}" : string.Empty;
        return ProgressReporter.TickAsync(
            () => $"  read {ByteSize.Describe(reader.BytesConsumed)}{ofTotal} ({clock.Elapsed.TotalSeconds:F1}s)",
            ct);
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
