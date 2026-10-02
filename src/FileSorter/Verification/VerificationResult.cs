namespace FileSorter.Verification;

internal readonly record struct FileScanReport(long LineCount, long ByteCount, ulong Hash);

internal enum VerificationOutcome
{
    Verified,
    OrderViolation,
    CountMismatch,
    HashMismatch,
    OutputNotTerminated,
}

// On OrderViolation the scan stops early, so Output.LineCount and Output.Hash are partial.
internal sealed record VerificationResult(
    VerificationOutcome Outcome,
    FileScanReport Input,
    FileScanReport Output,
    string? FailureDetail,
    long? OrderViolationLineNumber = null);
