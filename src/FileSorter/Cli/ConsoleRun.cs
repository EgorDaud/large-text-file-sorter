using FileSorter.LineFormat;
using FileSorter.Merging;
using Shared;

namespace FileSorter.Cli;

internal static class ConsoleRun
{
    internal static int Run(Func<CancellationToken, Task<int>> body)
    {
        using ConsoleCancellation cancellation = new();

        try
        {
            return body(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
        }
        catch (PreflightException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ex.ExitCode;
        }
        catch (DestinationReplaceFailedException ex)
        {
            // Deliberately an argument error, not IoFailure, though the inner failure is I/O.
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.InvalidArguments;
        }
        catch (MalformedLineException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.MalformedInput;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"I/O error: {ex.Message}");
            return ExitCodes.IoFailure;
        }
    }
}
