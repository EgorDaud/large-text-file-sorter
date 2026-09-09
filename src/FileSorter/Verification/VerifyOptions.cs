namespace FileSorter.Verification;

// Verification uses fixed scan buffers; maxLineLength bounds carry between reads.
internal sealed record VerifyOptions(string InputPath, string OutputPath, int MaxLineLength);
