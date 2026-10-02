using System.Diagnostics;
using Akka.Actor;
using Akka.Configuration;
using Akka.Streams;
using FileSorter.Cli;
using FileSorter.Infrastructure;
using FileSorter.Merging;
using FileSorter.Planning;
using FileSorter.RunGeneration;
using FileSorter.Verification;

namespace FileSorter;

// Dispatches commands, validates the sort, and runs its phases. ConsoleRun maps failures to exit codes.
internal static class Program
{
    // Sizes descriptor arrays without constraining valid input. A lower estimate uses more
    // descriptor memory; a higher one fills descriptors sooner and can increase carry copying.
    // 32 keeps byte buffers filling first on the generated benchmark data.
    private const int AssumedMeanLineLength = 32;

    private static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.Error.WriteLine(CommandLine.Usage);
            return ExitCodes.Success;
        }

        // Verification has its own option set.
        if (args is [ "--verify", .. ])
        {
            return VerifyCommand.Execute(args);
        }

        if (!CommandLine.TryParseOptions(args, out SorterOptions? options, out string? error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLine.Usage);
            return ExitCodes.InvalidArguments;
        }

        return ConsoleRun.Run(ct => RunAsync(options, ct));
    }

    internal static async Task<int> RunAsync(SorterOptions options, CancellationToken ct)
    {
        try
        {
            return await SortAsync(options, ct);
        }
        catch (PreflightException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ex.ExitCode;
        }
    }

    private static async Task<int> SortAsync(SorterOptions options, CancellationToken ct)
    {
        FileInfo inputInfo = ValidateInput(options.InputPath);
        MemoryPlan plan = PlanMemory(options);

        // Validate destinations before reading input or creating run files.
        string outputDirectory = Preflight.DirectoryOf(options.OutputPath);
        Preflight.ValidateOutputDirectory(outputDirectory);

        // Create the temp parent before probing its capacity. The private run directory
        // is created only when the first run is written.
        using TemporaryRunSet runs = Preflight.CreateTemporaryRunSet(options.TempDirectory);

        // The final output can occupy a different volume from the temporary runs.
        VolumeCapacity.Check(inputInfo.Length, options.TempDirectory, outputDirectory);

        Stopwatch clock = Stopwatch.StartNew();

        if (options.Pipeline is Pipeline.Akka)
        {
            // Suppress Akka's console logger to keep stdout empty on success.
            Config quiet = ConfigurationFactory.ParseString("akka.loglevel = OFF\nakka.stdout-loglevel = OFF");

            // The ActorSystem owns the materializer lifetime.
            using ActorSystem system = ActorSystem.Create("sorter", quiet);
            IMaterializer materializer = system.Materializer();
            await RunPhasesAsync(
                options, inputInfo, plan, runs,
                (reader, spill, parallelism, token) => AkkaRunGeneration.RunAsync(reader, spill, parallelism, materializer, token),
                clock, ct);
        }
        else
        {
            await RunPhasesAsync(options, inputInfo, plan, runs, ChannelRunGeneration.RunAsync, clock, ct);
        }

        Console.Error.WriteLine($"Sorted {ProgressReporter.Describe(inputInfo.Length)} in {clock.Elapsed.TotalSeconds:F1}s.");
        return ExitCodes.Success;
    }

    // Empty input needs an output file; a single sorted run needs no merge.
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

        // Only the merge writes directly to the output. The other branches use staging files.
        MergeExecutor? executor = null;
        try
        {
            // Honour a cancel that arrived after phase one, before any placement.
            ct.ThrowIfCancellationRequested();

            if (runPaths.Count <= 1)
            {
                try
                {
                    if (runPaths.Count == 0)
                    {
                        RunPlacement.PlaceEmpty(options.OutputPath);
                    }
                    else
                    {
                        RunPlacement.Place(runPaths[0], options.OutputPath, runs);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Include the requested output path in replacement errors.
                    throw new DestinationReplaceFailedException(options.OutputPath, ex);
                }
            }
            else
            {
                // Collect the now-unreachable phase-one pool before allocating phase-two buffers
                // from the same budget. Large pooled arrays require a generation-2 collection.
                GC.Collect();

                executor = new MergeExecutor(runs, plan, options.MaxLineLength);
                await ProgressReporter.RunWithProgressAsync(
                    progressCt => MergeReporter.ReportProgressAsync(executor, clock, progressCt),
                    () => executor.ExecuteAsync(runPaths, options.OutputPath, ct),
                    ct);

                MergeReporter.ReportSummary(executor);
            }
        }
        catch
        {
            // Remove incomplete merge output only if the executor opened it. A failure before
            // that point must leave any existing output untouched.
            if (executor is { OutputOpened: true })
            {
                StagingFile.Delete(options.OutputPath);
            }

            throw;
        }
    }

    private static FileInfo ValidateInput(string inputPath)
    {
        FileInfo inputInfo = new(inputPath);
        if (!inputInfo.Exists)
        {
            throw new PreflightException($"Input file not found: '{inputPath}'.", ExitCodes.InvalidArguments);
        }

        try
        {
            // Report inaccessible input before allocating the sort buffers.
            using FileStream probe = File.OpenRead(inputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PreflightException($"Input file '{inputPath}' cannot be read: {ex.Message}", ExitCodes.InvalidArguments);
        }

        return inputInfo;
    }

    private static MemoryPlan PlanMemory(SorterOptions options)
    {
        // Keep the descriptor-size estimate within the configured line limit.
        int assumedMeanLineLength = AssumedMeanFor(options);
        if (MemoryBudget.TryCalculate(
                options.MemoryBudgetBytes, options.Parallelism, options.MaxLineLength, assumedMeanLineLength, out MemoryPlan plan))
        {
            return plan;
        }

        // Report the minimum budget using CLI option names and units.
        long minimum = MemoryBudget.MinimumViableBudget(options.Parallelism, options.MaxLineLength, assumedMeanLineLength);
        throw new PreflightException(
            $"A --memory budget of {ProgressReporter.Describe(options.MemoryBudgetBytes)} is too small. At --parallelism " +
            $"{options.Parallelism} with --max-line {ProgressReporter.Describe(options.MaxLineLength)}, the sorter needs at " +
            $"least {ProgressReporter.Describe(minimum)}.",
            ExitCodes.InvalidArguments);
    }

    // Keep budget calculation and minimum-budget diagnostics on the same estimate.
    private static int AssumedMeanFor(SorterOptions options) =>
        Math.Min(AssumedMeanLineLength, options.MaxLineLength);
}
