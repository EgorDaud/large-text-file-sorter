namespace FileSorter.Cli;

internal static class ExitCodes
{
    public const int Success              = 0;
    public const int MalformedInput       = 1;
    public const int InsufficientTempSpace = 2;
    public const int InvalidArguments     = 3;
    public const int VerificationFailed   = 4;
    public const int IoFailure            = 5;

    // Shell convention: 128 + SIGINT.
    public const int Cancelled            = 130;
}
