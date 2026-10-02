namespace FileSorter.Cli;

// A sort that cannot start: a bad input, budget or directory, or too little space. The
// message is already worded for the user; Program.RunAsync prints it and exits with ExitCode.
internal sealed class PreflightException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
