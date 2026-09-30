using System.Runtime.InteropServices;
using FileSorter.LineFormat;

namespace FileSorter.Startup;

// Runs a command body under the process-level cancellation and failure policy that sort
// and verify share: the first Ctrl+C or SIGTERM unwinds through cleanup, a repeat ends
// the process at once, and the failures a run can meet map to exit codes.
internal static class ConsoleRun
{
    internal static int Run(Func<CancellationToken, Task<int>> body)
    {
        using CancellationTokenSource cts = new();

        // Only the first request is handled. Leaving Cancel unset on a repeat lets the
        // default action terminate the process while a slow cleanup is still running.
        bool BeginCancel()
        {
            if (cts.IsCancellationRequested)
            {
                return false;
            }

            cts.Cancel();
            return true;
        }

        ConsoleCancelEventHandler onCancelKey = (_, e) => e.Cancel = BeginCancel();
        Action<PosixSignalContext> onSignal = context => context.Cancel = BeginCancel();
        // SIGHUP is left alone so a sort started under nohup survives the session ending.
        using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, onSignal);
        Console.CancelKeyPress += onCancelKey;

        try
        {
            return body(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
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
        finally
        {
            Console.CancelKeyPress -= onCancelKey;
        }
    }
}
