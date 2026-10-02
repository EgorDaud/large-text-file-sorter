using FileSorter.Cli;
using FileSorter.LineFormat;
using FileSorter.Tests.Support;
using FileSorter.Verification;
using Xunit;
using static FileSorter.Tests.Support.TestTimeouts;

namespace FileSorter.Tests.EndToEnd;

[Collection("Program")]
public sealed class VerifyTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    [Trait("Case", "VF-01")]
    public async Task Verifying_a_genuinely_sorted_output_reports_success()
    {
        string inputPath = WriteFile("in.txt", "3. Cherry\n1. Apple\n2. Banana\n");
        string outputPath = WriteFile("out.txt", "1. Apple\n2. Banana\n3. Cherry\n");

        int exitCode = await RunVerifyAsync(inputPath, outputPath);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    [Trait("Case", "VF-02")]
    public async Task Verifying_an_out_of_order_pair_reports_the_right_line_number()
    {
        string inputPath = WriteFile("in.txt", "1. Apple\n2. Banana\n3. Cherry\n");
        string outputPath = WriteFile("out.txt", "1. Apple\n3. Cherry\n2. Banana\n");

        (int exitCode, VerificationResult result) = await RunVerifyWithResultAsync(inputPath, outputPath);

        Assert.Equal(4, exitCode);
        Assert.Equal(VerificationOutcome.OrderViolation, result.Outcome);
        Assert.Contains("line 3", result.FailureDetail);

        // The scan stops at the violation, so only lines 1 and 2 are counted.
        Assert.Equal(3, result.OrderViolationLineNumber);
        Assert.Equal(2, result.Output.LineCount);
    }

    [Fact]
    [Trait("Case", "VF-03")]
    public async Task Verifying_a_dropped_line_is_caught_by_the_count_and_hash_check()
    {
        string inputPath = WriteFile("in.txt", "1. Apple\n2. Banana\n3. Cherry\n");
        string outputPath = WriteFile("out.txt", "1. Apple\n3. Cherry\n");

        (int exitCode, VerificationResult result) = await RunVerifyWithResultAsync(inputPath, outputPath);

        Assert.Equal(4, exitCode);
        Assert.Equal(VerificationOutcome.CountMismatch, result.Outcome);
        Assert.Equal(3, result.Input.LineCount);
        Assert.Equal(2, result.Output.LineCount);
    }

    [Fact]
    [Trait("Case", "VF-04")]
    public async Task Verifying_a_duplicated_line_is_caught_by_the_hash_check_when_the_count_still_matches()
    {
        string inputPath = WriteFile("in.txt", "1. Apple\n2. Banana\n");
        string outputPath = WriteFile("out.txt", "1. Apple\n1. Apple\n");

        (int exitCode, VerificationResult result) = await RunVerifyWithResultAsync(inputPath, outputPath);

        Assert.Equal(4, exitCode);
        Assert.Equal(VerificationOutcome.HashMismatch, result.Outcome);
        Assert.Equal(result.Input.LineCount, result.Output.LineCount);
        Assert.NotEqual(result.Input.Hash, result.Output.Hash);
    }

    [Fact]
    [Trait("Case", "VF-05")]
    public async Task Verifying_a_crlf_input_against_an_lf_only_output_verifies()
    {
        string inputPath = WriteFile("in.txt", "2. Banana\r\n1. Apple\r\n");
        string outputPath = WriteFile("out.txt", "1. Apple\n2. Banana\n");

        int exitCode = await RunVerifyAsync(inputPath, outputPath);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    [Trait("Case", "VF-06")]
    public async Task Verifying_a_malformed_output_line_surfaces_MalformedLineException_naming_the_offending_line()
    {
        string inputPath = WriteFile("in.txt", "1. Apple\n2. Banana\n");
        string outputPath = WriteFile("out.txt", "1. Apple\nno separator here\n");

        MalformedLineException thrown = await Assert.ThrowsAsync<MalformedLineException>(() => RunVerifyAsync(inputPath, outputPath));

        Assert.Equal(2, thrown.LineNumber);
        Assert.Contains("no separator here", thrown.Message);
        Assert.Contains(outputPath, thrown.Message);
    }

    [Fact]
    [Trait("Case", "VF-08")]
    public async Task Verifying_a_malformed_input_line_names_the_input_path_not_the_output_path()
    {
        string inputPath = WriteFile("in.txt", "1. Apple\nno separator here\n");
        string outputPath = WriteFile("out.txt", "1. Apple\n2. Banana\n");

        MalformedLineException thrown = await Assert.ThrowsAsync<MalformedLineException>(() => RunVerifyAsync(inputPath, outputPath));

        Assert.Contains(inputPath, thrown.Message);
        Assert.DoesNotContain(outputPath, thrown.Message);
    }

    [Fact]
    [Trait("Case", "VF-07")]
    public async Task Verifying_an_input_whose_final_unterminated_line_ends_in_a_bare_carriage_return_verifies()
    {
        string inputPath = WriteFile("in.txt", "2. Banana\n1. Apple\r");
        string outputPath = WriteFile("out.txt", "1. Apple\n2. Banana\n");

        int exitCode = await RunVerifyAsync(inputPath, outputPath);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    [Trait("Case", "VF-09")]
    public async Task Verifying_output_whose_content_ends_in_a_carriage_return_before_the_line_feed_verifies()
    {
        // Input "1. a\r\r\n" has content "1. a\r"; output "1. a\r\n" is that content plus a bare '\n'.
        string inputPath = WriteFile("in.txt", "2. Banana\n1. a\r\r\n");
        string outputPath = WriteFile("out.txt", "2. Banana\n1. a\r\n");

        int exitCode = await RunVerifyAsync(inputPath, outputPath);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    [Trait("Case", "VF-10")]
    public async Task Verifying_an_output_whose_final_carriage_return_is_never_terminated_fails()
    {
        string inputPath = WriteFile("in.txt", "1. a\n");
        string outputPath = WriteFile("out.txt", "1. a\r");

        (int exitCode, VerificationResult result) = await RunVerifyWithResultAsync(inputPath, outputPath);

        Assert.Equal(4, exitCode);
        Assert.Equal(VerificationOutcome.OutputNotTerminated, result.Outcome);
        Assert.Contains("no trailing", result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Case", "VF-11")]
    public async Task Verifying_an_output_that_is_a_single_unterminated_carriage_return_fails_against_an_empty_input()
    {
        string inputPath = WriteFile("in.txt", "");
        string outputPath = WriteFile("out.txt", "\r");

        (int exitCode, VerificationResult result) = await RunVerifyWithResultAsync(inputPath, outputPath);

        Assert.Equal(4, exitCode);
        Assert.Equal(VerificationOutcome.OutputNotTerminated, result.Outcome);
    }

    [Fact]
    [Trait("Case", "VF-12")]
    public async Task Verifying_an_output_missing_its_final_line_feed_fails_even_without_a_trailing_carriage_return()
    {
        string inputPath = WriteFile("in.txt", "1. Apple\n2. Banana\n");
        string outputPath = WriteFile("out.txt", "1. Apple\n2. Banana");

        (int exitCode, VerificationResult result) = await RunVerifyWithResultAsync(inputPath, outputPath);

        Assert.Equal(4, exitCode);
        Assert.Equal(VerificationOutcome.OutputNotTerminated, result.Outcome);
    }

    [Fact]
    [Trait("Case", "VF-13")]
    public async Task Verifying_an_output_that_exists_but_cannot_be_read_exits_3_as_sort_mode_does()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FileShare.None blocks other opens only on Windows.");

        string inputPath = WriteFile("in.txt", "1. Apple\n");
        string outputPath = WriteFile("out.txt", "1. Apple\n");

        VerifyOptions options = new(inputPath, outputPath, MaxLineLength: 1024);
        int exitCode;
        string stderr;
        using (new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            (exitCode, stderr) = await ConsoleCapture.ErrorAsync(
                () => Task.Run(() => ConsoleRun.Run(ct => VerifyCommand.RunAsync(options, ct)))
                    .WaitAsync(BoundedWait, TestContext.Current.CancellationToken));
        }

        Assert.Equal(3, exitCode);
        Assert.Contains($"Output file '{outputPath}' cannot be read", stderr, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Case", "VF-14")]
    public async Task A_cancelled_verify_surfaces_the_cancellation_and_releases_both_files()
    {
        string inputPath = WriteFile("in.txt", "1. Apple\n2. Banana\n");
        string outputPath = WriteFile("out.txt", "1. Apple\n2. Banana\n");
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => VerifyCommand.RunAsync(new VerifyOptions(inputPath, outputPath, MaxLineLength: 1024), cts.Token)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        File.Delete(inputPath);
        File.Delete(outputPath);
        Assert.False(File.Exists(inputPath), "The input survived deletion, so a handle on it was still open.");
        Assert.False(File.Exists(outputPath), "The output survived deletion, so a handle on it was still open.");
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_directory.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static Task<int> RunVerifyAsync(string inputPath, string outputPath) =>
        VerifyCommand.RunAsync(
            new VerifyOptions(inputPath, outputPath, MaxLineLength: 1024), TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

    // Bypasses VerifyCommand to get the result; the exit code here is derived locally, not by production.
    private static async Task<(int ExitCode, VerificationResult Result)> RunVerifyWithResultAsync(string inputPath, string outputPath)
    {
        VerificationResult result = await OutputVerifier.RunAsync(
                inputPath, outputPath, maxLineLength: 1024, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        int exitCode = result.Outcome == VerificationOutcome.Verified ? 0 : 4;
        return (exitCode, result);
    }
}
