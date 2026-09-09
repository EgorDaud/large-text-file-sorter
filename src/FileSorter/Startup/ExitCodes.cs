namespace FileSorter.Startup;

// Shared process exit codes for sort and verify modes.
internal static class ExitCodes
{
    public const int Success              = 0;
    public const int MalformedInput       = 1;
    public const int InsufficientTempSpace = 2;
    public const int InvalidArguments     = 3;

    // 130 is the shell convention for cancellation. Verification uses 4 because its
    // arguments are valid even when output is unsorted or differs from input.
    public const int VerificationFailed   = 4;

    public const int Cancelled            = 130;
}
