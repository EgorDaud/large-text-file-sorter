namespace FileSorter.Infrastructure;

internal static class ProgressReporter
{
    public const int IntervalMilliseconds = 2000;

    public static async Task TickAsync(Func<string> line, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(IntervalMilliseconds, ct);
                Console.Error.WriteLine(line());
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // report must swallow its own cancellation, or the await in finally throws and masks work's outcome.
    public static async Task<T> RunWithProgressAsync<T>(
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

    public static async Task RunWithProgressAsync(
        Func<CancellationToken, Task> report, Func<Task> work, CancellationToken ct) =>
        await RunWithProgressAsync(report, async () =>
        {
            await work();
            return 0;
        }, ct);
}
