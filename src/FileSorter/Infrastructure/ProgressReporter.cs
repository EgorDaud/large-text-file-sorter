using System.Globalization;

namespace FileSorter.Infrastructure;

// Shared by every progress line: the reporting interval, the byte-count formatter, and the
// wrapper that runs a phase beside its periodic reporter.
internal static class ProgressReporter
{
    internal const int IntervalMilliseconds = 2000;

    private static readonly string[] SizeNames = ["B", "KiB", "MiB", "GiB"];

    // Starts report beside work and stops it when the work ends, however it ends. Reporters
    // swallow their own cancellation, so awaiting one after Cancel never throws.
    internal static async Task<T> RunWithProgressAsync<T>(
        Func<CancellationToken, Task> report, Func<Task<T>> work, CancellationToken ct)
    {
        using CancellationTokenSource progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task progressTask = report(progressCts.Token);
        try
        {
            return await work();
        }
        finally
        {
            progressCts.Cancel();
            await progressTask;
        }
    }

    internal static async Task RunWithProgressAsync(
        Func<CancellationToken, Task> report, Func<Task> work, CancellationToken ct) =>
        await RunWithProgressAsync(report, async () =>
        {
            await work();
            return 0;
        }, ct);

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
