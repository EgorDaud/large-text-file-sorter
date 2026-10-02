using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using TestFileGenerator.Generation;

namespace TestFileGenerator;

// Parses the command line, writes the file under the cancellation policy, and maps the
// outcome to an exit code. CommandLine owns parsing; StagedOutput owns the staged write.
internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitInvalidArguments = 3;
    private const int ExitCancelled = 130;

    private const int ProgressIntervalMilliseconds = 2000;

    // Report only units accepted by the parser.
    private static readonly string[] SizeNames = ["B", "KiB", "MiB", "GiB"];

    private static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.Error.WriteLine(CommandLine.Usage);
            return ExitSuccess;
        }

        if (!CommandLine.TryParseOptions(args, out GeneratorOptions? options, out string? error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLine.Usage);
            return ExitInvalidArguments;
        }

        // Validate composition before touching an existing output.
        LineComposer composer = new(options);

        Stopwatch clock = Stopwatch.StartNew();
        long nextReportMilliseconds = ProgressIntervalMilliseconds;

        // Mirrors the sorter's ConsoleRun, duplicated because the programs share no project.
        // The first Ctrl+C or SIGTERM cancels so the staging file is removed; a repeat ends
        // the process at once.
        using CancellationTokenSource cts = new();
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
        // SIGHUP is left alone so a run started under nohup survives the session ending.
        using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, onSignal);
        Console.CancelKeyPress += onCancelKey;

        long written;
        try
        {
            written = StagedOutput.Write(options, composer, ReportProgress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return ExitCancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Include the requested path in write failures.
            Console.Error.WriteLine($"Output file '{options.OutputPath}' could not be written: {ex.Message}");
            return ExitInvalidArguments;
        }
        finally
        {
            Console.CancelKeyPress -= onCancelKey;
        }

        double seconds = clock.Elapsed.TotalSeconds;
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Wrote {Describe(written)} to {options.OutputPath} in {seconds:F1}s."));

        return ExitSuccess;

        void ReportProgress(long bytesWritten)
        {
            if (clock.ElapsedMilliseconds < nextReportMilliseconds)
            {
                return;
            }

            nextReportMilliseconds = clock.ElapsedMilliseconds + ProgressIntervalMilliseconds;
            Console.Error.WriteLine($"  {Describe(bytesWritten)} of {Describe(options.TargetBytes)}");
        }
    }

    private static string Describe(long bytes)
    {
        double value = bytes;
        int name = 0;

        // Round before choosing a unit for stable boundary display.
        while (Math.Round(value, 1) >= 1024 && name < SizeNames.Length - 1)
        {
            value /= 1024;
            name++;
        }

        return name == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:F1} {SizeNames[name]}");
    }
}
