using System.Diagnostics;
using FileSorter.Startup;

namespace FileSorter.Merging;

// Runs phase two and its periodic progress reporter.
internal static class MergeDriver
{
    internal static async Task RunPhaseTwoAsync(
        MergeExecutor executor,
        IReadOnlyList<string> runPaths,
        string outputPath,
        Stopwatch clock,
        CancellationToken ct)
    {
        using CancellationTokenSource progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task progressTask = ProgressReporter.ReportMergeProgressAsync(executor, clock, progressCts.Token);
        try
        {
            await executor.ExecuteAsync(runPaths, outputPath, ct);
        }
        finally
        {
            progressCts.Cancel();
            try
            {
                await progressTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
