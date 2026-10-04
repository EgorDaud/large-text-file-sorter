using FileSorter.Cli;
using FileSorter.LineFormat;
using FileSorter.Tests.Support;
using Shared;
using Xunit;

namespace FileSorter.Tests.Cli;

// Run writes to the process-wide Console.Error, which other "Program" tests swap out.
[Collection("Program")]
public sealed class ConsoleRunTests
{
    [Fact]
    [Trait("Case", "CR-01")]
    public void A_body_that_returns_an_exit_code_has_it_passed_through_with_a_live_token()
    {
        bool cancellationRequested = true;

        int exitCode = ConsoleRun.Run(ct =>
        {
            cancellationRequested = ct.IsCancellationRequested;
            return Task.FromResult(4);
        });

        Assert.Equal(4, exitCode);
        Assert.False(cancellationRequested);
    }

    [Fact]
    [Trait("Case", "CR-02")]
    public void An_IOException_maps_to_exit_5_with_a_one_line_message()
    {
        (int exitCode, string stderr) = RunCaptured(_ => throw new IOException("There is not enough space on the disk."));

        Assert.Equal(5, exitCode);
        Assert.Equal("I/O error: There is not enough space on the disk." + Environment.NewLine, stderr);
    }

    [Fact]
    [Trait("Case", "CR-03")]
    public void An_UnauthorizedAccessException_maps_to_exit_5_with_a_one_line_message()
    {
        (int exitCode, string stderr) = RunCaptured(_ => throw new UnauthorizedAccessException("Access denied."));

        Assert.Equal(5, exitCode);
        Assert.Equal("I/O error: Access denied." + Environment.NewLine, stderr);
    }

    [Fact]
    [Trait("Case", "CR-04")]
    public void An_OperationCanceledException_maps_to_exit_130_silently()
    {
        (int exitCode, string stderr) = RunCaptured(_ => throw new OperationCanceledException());

        Assert.Equal(130, exitCode);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    [Trait("Case", "CR-05")]
    public void A_MalformedLineException_maps_to_exit_1_printing_its_message_unchanged()
    {
        MalformedLineException failure = new(byteOffset: 9, lineNumber: 2, preview: "no separator here");

        (int exitCode, string stderr) = RunCaptured(_ => throw failure);

        Assert.Equal(1, exitCode);
        Assert.Equal(failure.Message + Environment.NewLine, stderr);
    }

    [Fact]
    [Trait("Case", "CR-06")]
    public void An_exception_with_no_exit_code_propagates()
    {
        Assert.Throws<InvalidOperationException>(() => ConsoleRun.Run(_ => throw new InvalidOperationException("bug")));
    }

    [Fact]
    [Trait("Case", "CR-07")]
    public void A_PreflightException_maps_to_its_own_exit_code_printing_its_message_unchanged()
    {
        PreflightException failure = new("Not enough space on the temp volume.", ExitCodes.InsufficientTempSpace);

        (int exitCode, string stderr) = RunCaptured(_ => throw failure);

        Assert.Equal(2, exitCode);
        Assert.Equal(failure.Message + Environment.NewLine, stderr);
    }

    private static (int ExitCode, string Stderr) RunCaptured(Func<CancellationToken, Task<int>> body) =>
        ConsoleCapture.Error(() => ConsoleRun.Run(body));
}
