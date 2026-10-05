using TestFileGenerator.Generation;
using Xunit;

namespace TestFileGenerator.Tests.Generation;

public sealed class FileWriterTests
{
    private const long TargetBytes = 1024 * 1024;

    [Fact]
    public void Writing_when_the_target_is_reached_reports_the_bytes_it_put_on_the_stream()
    {
        using MemoryStream sink = new();

        long reported = new FileWriter(GeneratedOutput.Composer(), TargetBytes).Write(sink, TestContext.Current.CancellationToken);

        Assert.Equal(sink.Length, reported);
    }

    [Fact]
    public void Writing_when_progress_is_observed_reports_a_count_that_only_grows()
    {
        List<long> reports = [];
        using MemoryStream sink = new();

        long written = new FileWriter(GeneratedOutput.Composer(), TargetBytes).Write(sink, TestContext.Current.CancellationToken, reports.Add);

        Assert.NotEmpty(reports);
        Assert.Equal(reports.Order(), reports);
        Assert.All(reports, report => Assert.InRange(report, 1, written));
    }
}
