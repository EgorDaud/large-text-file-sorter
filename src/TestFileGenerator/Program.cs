using System.Diagnostics;
using System.Globalization;
using Shared;
using TestFileGenerator.Generation;

namespace TestFileGenerator;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitInvalidArguments = 3;
    private const int ExitCancelled = 130;

    private const int ProgressIntervalMilliseconds = 2000;

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

        LineComposer composer = new(options);

        Stopwatch clock = Stopwatch.StartNew();
        long nextReportMilliseconds = ProgressIntervalMilliseconds;

        using ConsoleCancellation cancellation = new();

        long written;
        try
        {
            written = StagedOutput.Write(options, composer, ReportProgress, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return ExitCancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Output file '{options.OutputPath}' could not be written: {ex.Message}");
            return ExitInvalidArguments;
        }

        double seconds = clock.Elapsed.TotalSeconds;
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Wrote {ByteSize.Describe(written)} to {options.OutputPath} in {seconds:F1}s."));

        return ExitSuccess;

        void ReportProgress(long bytesWritten)
        {
            if (clock.ElapsedMilliseconds < nextReportMilliseconds)
            {
                return;
            }

            nextReportMilliseconds = clock.ElapsedMilliseconds + ProgressIntervalMilliseconds;
            Console.Error.WriteLine($"  {ByteSize.Describe(bytesWritten)} of {ByteSize.Describe(options.TargetBytes)}");
        }
    }
}
