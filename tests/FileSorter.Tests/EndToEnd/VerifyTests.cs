using FileSorter.LineFormat;
using FileSorter.Verification;
using Xunit;

namespace FileSorter.Tests.EndToEnd;

// Drives VerifyCommand.RunAsync directly, the entry point --verify uses, with the same
// discipline SortRoundTripTests applies to sort mode.
[Collection("Program")]
public sealed class VerifyTests : IDisposable
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "FileSorterTests", Guid.NewGuid().ToString("N"));

    public VerifyTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

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

        // Line 3 sorts before line 2 under the string-part-ascending rule, so the
        // violation is detected between output lines 2 and 3 -- line number 3 is
        // the one that is out of place relative to what came before it.
        string outputPath = WriteFile("out.txt", "1. Apple\n3. Cherry\n2. Banana\n");

        (int exitCode, VerificationResult result) = await RunVerifyWithResultAsync(inputPath, outputPath);

        Assert.Equal(4, exitCode);
        Assert.Equal(VerificationOutcome.OrderViolation, result.Outcome);
        Assert.Contains("line 3", result.FailureDetail);

        // OrderViolationLineNumber is what tells Program not to print
        // Output.LineCount/Hash as though the scan had read the whole file: the scan
        // stopped at the violation, one line short of the output's real three. Only
        // lines 1 and 2 were folded into the hash, which is therefore partial.
        Assert.Equal(3, result.OrderViolationLineNumber);
        Assert.Equal(2, result.Output.LineCount);
    }

    [Fact]
    [Trait("Case", "VF-03")]
    public async Task Verifying_a_dropped_line_is_caught_by_the_count_and_hash_check()
    {
        string inputPath = WriteFile("in.txt", "1. Apple\n2. Banana\n3. Cherry\n");
        string outputPath = WriteFile("out.txt", "1. Apple\n3. Cherry\n"); // "2. Banana" dropped, still sorted

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

        // "1. Apple" duplicated in place of "2. Banana": same line count as the
        // input, still non-decreasing, so only the order-independent hash --
        // not the count and not the adjacent-order check -- can catch this.
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
        // Input terminators are normalised -- one stripped CR per \r\n -- exactly as
        // the sorter normalises them for hashing, so a \r\n input and its correctly
        // sorted \n-only output agree.
        string inputPath = WriteFile("in.txt", "2. Banana\r\n1. Apple\r\n");
        string outputPath = WriteFile("out.txt", "1. Apple\n2. Banana\n");

        int exitCode = await RunVerifyAsync(inputPath, outputPath);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    [Trait("Case", "VF-06")]
    public async Task Verifying_a_malformed_output_line_surfaces_MalformedLineException_naming_the_offending_line()
    {
        // RunVerifyAsync lets MalformedLineException through unmapped, exactly as sort
        // mode's RunAsync does; RunVerifyMode, one layer up, maps it to exit 1, the
        // same code sort mode uses for the same defect.
        string inputPath = WriteFile("in.txt", "1. Apple\n2. Banana\n");
        string outputPath = WriteFile("out.txt", "1. Apple\nno separator here\n");

        MalformedLineException thrown = await Assert.ThrowsAsync<MalformedLineException>(() => RunVerifyAsync(inputPath, outputPath));

        Assert.Equal(2, thrown.LineNumber);
        Assert.Contains("no separator here", thrown.Message);

        // The message names the output path, not just the offset and the preview:
        // --verify reads two files in one run, and without the path an operator cannot
        // tell from the exception which one failed.
        Assert.Contains(outputPath, thrown.Message);
    }

    [Fact]
    [Trait("Case", "VF-08")]
    public async Task Verifying_a_malformed_input_line_names_the_input_path_not_the_output_path()
    {
        // The same labelling, pinned on the other file: a malformed input line must
        // name in.txt, not out.txt, so the two failure modes stay distinguishable.
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
        // OutputVerifier's reader must apply the same finalisation rule ChunkReader and
        // RunCursor do, or a correct sort of this input reports a hash mismatch against
        // itself: the input's reading of "1. Apple\r" has to agree with the output's
        // reading of the "1. Apple\n" the sorter produced from it.
        string inputPath = WriteFile("in.txt", "2. Banana\n1. Apple\r");
        string outputPath = WriteFile("out.txt", "1. Apple\n2. Banana\n");

        int exitCode = await RunVerifyAsync(inputPath, outputPath);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    [Trait("Case", "VF-09")]
    public async Task Verifying_output_whose_content_ends_in_a_carriage_return_before_the_line_feed_verifies()
    {
        // The output side's scan must not strip a '\r' immediately before '\n': run
        // files and the sorter's output use a bare '\n' terminator throughout, so that
        // byte is always content there, never a terminator's second half. Input
        // "1. a\r\r\n" parses -- strip exactly one '\r' before '\n' -- to content
        // "1. a\r"; the correctly sorted output keeps that content terminated by a bare
        // '\n', "1. a\r\n", which the output scan must read back intact rather than
        // stripping the '\r' a second time and disagreeing with the input's hash.
        string inputPath = WriteFile("in.txt", "2. Banana\n1. a\r\r\n");
        string outputPath = WriteFile("out.txt", "2. Banana\n1. a\r\n");

        int exitCode = await RunVerifyAsync(inputPath, outputPath);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    [Trait("Case", "VF-10")]
    public async Task Verifying_an_output_whose_final_carriage_return_is_never_terminated_fails()
    {
        // The input side's bare-CR-at-EOF allowance must not be applied to the output
        // side. That allowance exists because an INPUT file is permitted to arrive
        // without a final terminator; the sorter's own output never is, because it
        // terminates its last line unconditionally. Applied to the output, it lets
        // "1. a\r" (no final '\n' at all) verify against input "1. a\n" -- both scans
        // hash "1. a" and the missing terminator goes unreported.
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
        // The degenerate case of the same rule: an output that is nothing but one bare,
        // unterminated '\r'. An empty input is valid and produces empty output, never
        // this.
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
        // The general case behind the two carriage-return-shaped ones above: ANY output
        // whose last line never reached a '\n' is not a valid sorter output, because
        // every emitted line is terminated including the last, independent of what byte
        // it happens to end on.
        string inputPath = WriteFile("in.txt", "1. Apple\n2. Banana\n");
        string outputPath = WriteFile("out.txt", "1. Apple\n2. Banana");

        (int exitCode, VerificationResult result) = await RunVerifyWithResultAsync(inputPath, outputPath);

        Assert.Equal(4, exitCode);
        Assert.Equal(VerificationOutcome.OutputNotTerminated, result.Outcome);
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static Task<int> RunVerifyAsync(string inputPath, string outputPath) =>
        VerifyCommand.RunAsync(
            new VerifyOptions(inputPath, outputPath, MaxLineLength: 1024), TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

    // Most cases only need the exit code; the ones pinning which check actually fired
    // need the VerificationResult itself. RunVerifyAsync returns only an int, the same
    // as sort mode's RunAsync, so this drives OutputVerifier directly instead.
    private static async Task<(int ExitCode, VerificationResult Result)> RunVerifyWithResultAsync(string inputPath, string outputPath)
    {
        VerifyOptions options = new(inputPath, outputPath, MaxLineLength: 1024);
        VerificationResult result = await OutputVerifier.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        int exitCode = result.Outcome == VerificationOutcome.Verified ? 0 : 4;
        return (exitCode, result);
    }
}
