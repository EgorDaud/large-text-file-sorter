namespace FileSorter.Cli;

internal sealed class PreflightException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
