using System.Diagnostics;
using System.Globalization;
using FileSorter.Infrastructure;
using Shared;

namespace FileSorter.Merging;

internal static class MergeReporter
{
    public static Task ReportProgressAsync(MergeExecutor executor, Stopwatch clock, CancellationToken ct) =>
        ProgressReporter.TickAsync(
            () =>
            {
                // Clamp: a final tick can arrive after the last pass completes, before cancellation.
                int totalPasses = executor.PlannedPasses;
                int currentPass = Math.Min(executor.PassesExecuted + 1, totalPasses);

                return $"  merge pass {currentPass} of {totalPasses}: {ByteSize.Describe(executor.BytesWritten)} of " +
                    $"{ByteSize.Describe(executor.TotalBytesToWrite)} ({clock.Elapsed.TotalSeconds:F1}s)";
            },
            ct);

    public static void ReportSummary(MergeExecutor executor)
    {
        ReportShape(executor);
        Console.Error.WriteLine(
            executor.Partition is { Workers: > 1 } partition
                ? $"  merge waited {executor.OutputWaitSeconds:F1}s on output writes (summed across {partition.Workers} workers)"
                : $"  merge waited {executor.OutputWaitSeconds:F1}s on output writes");
    }

    private static void ReportShape(MergeExecutor executor)
    {
        if (executor.Partition is not { } stats)
        {
            Console.Error.WriteLine("  merge parallelism 1");
        }
        else if (stats.Outcome is PartitionOutcome.Parallel)
        {
            string imbalance = double.IsPositiveInfinity(stats.Imbalance)
                ? "n/a (an empty slice)"
                : string.Create(CultureInfo.InvariantCulture, $"{stats.Imbalance:F2}x");

            string sparse = stats.Sparse ? "sparse preallocation" : "plain preallocation";
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  merge parallelism {stats.Workers}: splitters located in " +
                $"{stats.SplitterSeconds:F2}s, slice imbalance {imbalance}, " +
                $"workers {stats.QuickestWorkerSeconds:F1}-{stats.SlowestWorkerSeconds:F1}s, {sparse}"));
        }
        else if (stats.Outcome is PartitionOutcome.SingleSlice)
        {
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  merge parallelism 1 (sampling cost {stats.SplitterSeconds:F2}s; " +
                $"partition left every line to one slice, merged sequentially)"));
        }
        else
        {
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  merge parallelism 1 (sampling cost {stats.SplitterSeconds:F2}s; no partition found)"));
        }
    }
}
