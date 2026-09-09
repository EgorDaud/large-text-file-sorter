using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Akka.Actor;
using Akka.Configuration;
using Akka.Streams;
using FileSorter.LineFormat;
using FileSorter.Merging;
using FileSorter.RunGeneration;
using FileSorter.Startup;
using FileSorter.Verification;

namespace FileSorter;

// Dispatches commands, runs the sort phases, and maps failures to exit codes.
internal static class Program
{
    // Labels destination replacement failures for exit 3. Merge write failures propagate separately.
    private sealed class DestinationReplaceFailedException(string outputPath, Exception inner)
        : Exception($"Output file '{outputPath}' could not be written: {inner.Message}", inner);

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

        using CancellationTokenSource cts = new();

        // Allow cancellation to unwind resource owners before the process exits.
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            return RunAsync(options, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
        }
        catch (MalformedLineException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.MalformedInput;
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "budgetBytes")
        {
            // Report the minimum budget using CLI option names and units.
            long minimum = MemoryBudget.MinimumViableBudget(
                options.Parallelism, options.MaxLineLength, AssumedMeanFor(options));

            Console.Error.WriteLine(
                $"A --memory budget of {ProgressReporter.Describe(options.MemoryBudgetBytes)} is too small. At --parallelism " +
                $"{options.Parallelism} with --max-line {ProgressReporter.Describe(options.MaxLineLength)}, the sorter needs at " +
                $"least {ProgressReporter.Describe(minimum)}.");
            return ExitCodes.InvalidArguments;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    [SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "TryCreateTemporaryRunSet either hands back a run set that the `using` below " +
        "takes ownership of, or returns false having set it to null. CA2000 does not follow ownership " +
        "out of a Try-pattern out parameter.")]
    internal static async Task<int> RunAsync(SorterOptions options, CancellationToken ct)
    {
        FileInfo inputInfo = new(options.InputPath);
        if (!inputInfo.Exists)
        {
            Console.Error.WriteLine($"Input file not found: '{options.InputPath}'.");
            return ExitCodes.InvalidArguments;
        }

        try
        {
            // Report inaccessible input before allocating the sort buffers.
            using FileStream probe = File.OpenRead(options.InputPath);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Input file '{options.InputPath}' cannot be read: {ex.Message}");
            return ExitCodes.InvalidArguments;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Input file '{options.InputPath}' cannot be read: {ex.Message}");
            return ExitCodes.InvalidArguments;
        }

        // Keep the descriptor-size estimate within the configured line limit.
        int assumedMeanLineLength = AssumedMeanFor(options);
        MemoryPlan plan = MemoryBudget.Calculate(options.MemoryBudgetBytes, options.Parallelism, options.MaxLineLength, assumedMeanLineLength);

        // Validate destinations before reading input or creating run files.
        string outputDirectory = CapacityProbe.DirectoryOf(options.OutputPath);
        if (!CapacityProbe.TryValidateOutputDirectory(outputDirectory, out string? outputDirectoryError))
        {
            Console.Error.WriteLine(outputDirectoryError);
            return ExitCodes.InvalidArguments;
        }

        // Create the temp parent before probing its capacity. The private run directory
        // is created only when the first run is written.
        if (!CapacityProbe.TryCreateTemporaryRunSet(options.TempDirectory, out TemporaryRunSet? runsOrNull, out string? tempDirectoryError))
        {
            Console.Error.WriteLine(tempDirectoryError);
            return ExitCodes.InvalidArguments;
        }

        using TemporaryRunSet runs = runsOrNull;

        // The final output can occupy a different volume from the temporary runs.
        if (CapacityProbe.CheckCapacity(inputInfo.Length, options.TempDirectory, outputDirectory) is int capacityExitCode)
        {
            return capacityExitCode;
        }

        Stopwatch clock = Stopwatch.StartNew();

        // Let RunPlacement fall back to a copy when the move fails.
        Func<string, string, bool> tryMove = (from, to) =>
        {
            try
            {
                File.Move(from, to);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        };

        try
        {
            if (options.Pipeline is Pipeline.Akka)
            {
                // Suppress Akka's console logger to keep stdout empty on success.
                Config quiet = ConfigurationFactory.ParseString("akka.loglevel = OFF\nakka.stdout-loglevel = OFF");

                // The ActorSystem owns the materializer lifetime.
                using ActorSystem system = ActorSystem.Create("sorter", quiet);
                IMaterializer materializer = system.Materializer();
                await RunPhasesAsync(
                    options, inputInfo, plan, runs, tryMove,
                    (reader, spill, parallelism, token) => AkkaRunGeneration.RunAsync(reader, spill, parallelism, materializer, token),
                    clock, ct);
            }
            else
            {
                await RunPhasesAsync(options, inputInfo, plan, runs, tryMove, ChannelRunGeneration.RunAsync, clock, ct);
            }
        }
        catch (DestinationReplaceFailedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.InvalidArguments;
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
        Func<string, string, bool> tryMove,
        RunGenerationStrategy strategy,
        Stopwatch clock,
        CancellationToken ct)
    {
        IReadOnlyList<string> runPaths =
            await RunGenerationDriver.GenerateRunsAsync(options, inputInfo, plan, runs, strategy, clock, ct);

        Console.Error.WriteLine($"  phase one produced {runPaths.Count} run(s) ({clock.Elapsed.TotalSeconds:F1}s)");

        // Only the merge writes directly to the output. The other branches use staging files.
        MergeExecutor? executor = null;
        try
        {
            if (runPaths.Count == 0)
            {
                string staging = StagingFile.CreatePath(options.OutputPath);
                try
                {
                    using (new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                    }

                    try
                    {
                        File.Move(staging, options.OutputPath, overwrite: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Include the requested output path in replacement errors.
                        throw new DestinationReplaceFailedException(options.OutputPath, ex);
                    }
                }
                catch
                {
                    StagingFile.Delete(staging);
                    throw;
                }
            }
            else if (runPaths.Count == 1)
            {
                try
                {
                    RunPlacement.Place(runPaths[0], options.OutputPath, tryMove);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new DestinationReplaceFailedException(options.OutputPath, ex);
                }
            }
            else
            {
                // Collect the now-unreachable phase-one pool before allocating phase-two buffers
                // from the same budget. Large pooled arrays require a generation-2 collection.
                GC.Collect();

                executor = new MergeExecutor(runs, plan, options.MaxLineLength);
                await MergeDriver.RunPhaseTwoAsync(executor, runPaths, options.OutputPath, clock, ct);

                // Report actual workers: a multi-pass merge may use fewer than the plan allows.
                ProgressReporter.ReportMergeShape(executor);
                Console.Error.WriteLine(
                    executor.MergeParallelismUsed > 1
                        ? $"  merge waited {executor.OutputWaitSeconds:F1}s on output writes (summed across {executor.MergeParallelismUsed} workers)"
                        : $"  merge waited {executor.OutputWaitSeconds:F1}s on output writes");
            }
        }
        catch
        {
            // Remove incomplete merge output only if the executor opened it. A failure before
            // that point must leave any existing output untouched.
            if (executor is { OutputOpened: true })
            {
                DeletePartialOutput(options.OutputPath);
            }

            throw;
        }
    }

    // Keep budget calculation and minimum-budget diagnostics on the same estimate.
    private static int AssumedMeanFor(SorterOptions options) =>
        Math.Min(AssumedMeanLineLength, options.MaxLineLength);

    // Report cleanup failures without replacing the original error.
    private static void DeletePartialOutput(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception removal)
        {
            Console.Error.WriteLine($"Could not remove the partial output at {path}: {removal.Message}");
        }
    }
}
