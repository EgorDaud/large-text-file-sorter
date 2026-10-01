using System.Diagnostics;
using System.Globalization;
using FileSorter.Startup;

namespace FileSorter.Merging;

// Phase two's stderr lines: the periodic progress line and the end-of-merge shape summary.
internal static class MergeReporter
{
    internal static async Task ReportProgressAsync(MergeExecutor executor, Stopwatch clock, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ProgressReporter.IntervalMilliseconds, ct);

                // Completed passes are zero-based for the active pass. Clamp the final
                // tick that can arrive before cancellation.
                int totalPasses = executor.PlannedPasses;
                int currentPass = Math.Min(executor.PassesExecuted + 1, totalPasses);

                // MergeProgress reads the changing byte count with Volatile.Read.
                Console.Error.WriteLine(
                    $"  merge pass {currentPass} of {totalPasses}: {ProgressReporter.Describe(executor.BytesWritten)} of " +
                    $"{ProgressReporter.Describe(executor.TotalBytesToWrite)} ({clock.Elapsed.TotalSeconds:F1}s)");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Reports actual merge parallelism, splitter cost, slice balance, and preallocation.
    internal static void ReportShape(MergeExecutor executor)
    {
        if (executor.Partition is not { } stats)
        {
            Console.Error.WriteLine("  merge parallelism 1");
        }
        else if (stats.Workers > 1)
        {
            // An empty slice yields infinity, which is clearer as text than "∞x".
            string imbalance = double.IsPositiveInfinity(stats.Imbalance)
                ? "n/a (an empty slice)"
                : string.Create(CultureInfo.InvariantCulture, $"{stats.Imbalance:F2}x");

            string sparse = stats.Sparse ? "sparse preallocation" : "plain preallocation";
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  merge parallelism {stats.Workers}: splitters located in " +
                $"{stats.SplitterSeconds:F2}s, slice imbalance {imbalance}, " +
                $"workers {stats.QuickestWorkerSeconds:F1}-{stats.SlowestWorkerSeconds:F1}s, {sparse}"));
        }
        else if (double.IsPositiveInfinity(stats.Imbalance))
        {
            // The attempt sampled a partition with an empty slice (for example all-equal
            // keys) and merged sequentially instead.
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  merge parallelism 1 (sampling cost {stats.SplitterSeconds:F2}s; " +
                $"partition had an empty slice, merged sequentially)"));
        }
        else
        {
            // Sampling can run even when no partition is available; keep that cost visible.
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  merge parallelism 1 (sampling cost {stats.SplitterSeconds:F2}s; no partition found)"));
        }
    }
}
