using System.Diagnostics;
using System.Globalization;
using FileSorter.Merging;
using FileSorter.RunGeneration;

namespace FileSorter.Startup;

// Reports phase progress, merge shape, and shared human-readable byte counts.
internal static class ProgressReporter
{
    private const int ProgressIntervalMilliseconds = 2000;

    private static readonly string[] SizeNames = ["B", "KiB", "MiB", "GiB"];

    // Phase one exposes only BytesConsumed. Supported 64-bit targets read its aligned
    // long atomically, so reporting can read it directly.
    internal static async Task ReportReadProgressAsync(
        ChunkReader reader, long inputSizeBytes, Stopwatch clock, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ProgressIntervalMilliseconds, ct);

                long consumed = reader.BytesConsumed;
                string ofTotal = inputSizeBytes > 0 ? $" of {Describe(inputSizeBytes)}" : string.Empty;
                Console.Error.WriteLine($"  read {Describe(consumed)}{ofTotal} ({clock.Elapsed.TotalSeconds:F1}s)");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Reports actual merge parallelism, splitter cost, slice balance, and preallocation.
    internal static void ReportMergeShape(MergeExecutor executor)
    {
        if (executor.MergeParallelismUsed > 1)
        {
            // An empty slice yields infinity, which is clearer as text than "∞x".
            string imbalance = double.IsPositiveInfinity(executor.PartitionImbalance)
                ? "n/a (an empty slice)"
                : string.Create(CultureInfo.InvariantCulture, $"{executor.PartitionImbalance:F2}x");

            string sparse = executor.OutputMarkedSparse ? "sparse preallocation" : "plain preallocation";
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  merge parallelism {executor.MergeParallelismUsed}: splitters located in " +
                $"{executor.SplitterSeconds:F2}s, slice imbalance {imbalance}, " +
                $"workers {executor.QuickestWorkerSeconds:F1}-{executor.SlowestWorkerSeconds:F1}s, {sparse}"));
        }
        else if (executor.SplitterSeconds > 0)
        {
            // Sampling can run even when no partition is available; keep that cost visible.
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  merge parallelism 1 (sampling cost {executor.SplitterSeconds:F2}s; no partition found)"));
        }
        else
        {
            Console.Error.WriteLine("  merge parallelism 1");
        }
    }

    internal static async Task ReportMergeProgressAsync(MergeExecutor executor, Stopwatch clock, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ProgressIntervalMilliseconds, ct);

                // Completed passes are zero-based for the active pass. Clamp the final
                // tick that can arrive before cancellation.
                int totalPasses = executor.PlannedPasses;
                int currentPass = Math.Min(executor.PassesExecuted + 1, totalPasses);

                // MergeProgress reads the changing byte count with Volatile.Read.
                Console.Error.WriteLine(
                    $"  merge pass {currentPass} of {totalPasses}: {Describe(executor.BytesWritten)} of " +
                    $"{Describe(executor.TotalBytesToWrite)} ({clock.Elapsed.TotalSeconds:F1}s)");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static string Describe(long bytes)
    {
        double value = bytes;
        int name = 0;

        // Round before choosing a unit so near-boundary values read as 1.0 GiB.
        while (Math.Round(value, 1) >= 1024 && name < SizeNames.Length - 1)
        {
            value /= 1024;
            name++;
        }

        return name == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:F1} {SizeNames[name]}");
    }
}
