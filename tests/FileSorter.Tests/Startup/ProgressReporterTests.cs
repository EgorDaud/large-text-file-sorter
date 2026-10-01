using FileSorter.Startup;
using Xunit;

namespace FileSorter.Tests.Startup;

// Drives RunWithProgressAsync with a stand-in reporter that records when it was cancelled and
// when it finished. Its cleanup takes a moment, so it has finished by the time the call
// returns only if the helper awaited it.
public sealed class ProgressReporterTests
{
    private sealed class FakeReporter
    {
        public bool Started { get; private set; }

        public bool CancelledWhenStarted { get; private set; }

        public bool Cancelled { get; private set; }

        public bool Finished { get; private set; }

        public async Task ReportAsync(CancellationToken ct)
        {
            Started = true;
            CancelledWhenStarted = ct.IsCancellationRequested;
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
            }

            await Task.Delay(50, CancellationToken.None);
            Finished = true;
        }
    }

    [Fact]
    [Trait("Case", "PR-01")]
    public async Task The_work_result_is_returned_and_the_reporter_is_cancelled_and_awaited_afterwards()
    {
        FakeReporter reporter = new();
        bool reporterRunningDuringWork = false;

        int result = await ProgressReporter.RunWithProgressAsync(
            reporter.ReportAsync,
            async () =>
            {
                await Task.Yield();
                reporterRunningDuringWork = reporter.Started && !reporter.Cancelled;
                return 42;
            },
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.True(reporterRunningDuringWork);
        Assert.False(reporter.CancelledWhenStarted);
        Assert.True(reporter.Cancelled);
        Assert.True(reporter.Finished);
    }

    [Fact]
    [Trait("Case", "PR-02")]
    public async Task A_throwing_work_still_cancels_and_awaits_the_reporter_and_its_exception_propagates()
    {
        FakeReporter reporter = new();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProgressReporter.RunWithProgressAsync(
                reporter.ReportAsync,
                () => Task.FromException<int>(new InvalidOperationException("work failed")),
                CancellationToken.None));

        Assert.Equal("work failed", ex.Message);
        Assert.True(reporter.Cancelled);
        Assert.True(reporter.Finished);
    }

    [Fact]
    [Trait("Case", "PR-03")]
    public async Task The_resultless_overload_cancels_and_awaits_the_reporter_too()
    {
        FakeReporter reporter = new();
        bool workRan = false;

        await ProgressReporter.RunWithProgressAsync(
            reporter.ReportAsync,
            async () =>
            {
                await Task.Yield();
                workRan = true;
            },
            CancellationToken.None);

        Assert.True(workRan);
        Assert.True(reporter.Cancelled);
        Assert.True(reporter.Finished);
    }
}
