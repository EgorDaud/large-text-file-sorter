namespace FileSorter.Verification;

internal readonly record struct FileScanReport(long LineCount, long ByteCount, ulong Hash);

internal enum VerificationOutcome
{
    Verified,
    OrderViolation,
    CountMismatch,
    HashMismatch,

    // The sorter always terminates output lines, including the last one.
    OutputNotTerminated,
}

// FailureDetail is null on success. An order violation stops the output scan early:
// Output.LineCount and Output.Hash are partial, while Output.ByteCount is the file size.
// Report OrderViolationLineNumber instead of presenting partial totals as final.
internal sealed record VerificationResult(
    VerificationOutcome Outcome,
    FileScanReport Input,
    FileScanReport Output,
    string? FailureDetail,
    long? OrderViolationLineNumber = null);
