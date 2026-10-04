using System.Diagnostics;
using System.Globalization;
using Shared;
using TestFileGenerator.Generation;

namespace TestFileGenerator;

internal static class GenerateCommand
{
    private const int ProgressIntervalMilliseconds = 2000;

    public static int Execute(GeneratorOptions options)
    {
        using ConsoleCancellation cancellation = new();
        return Run(options, cancellation.Token);
    }

    public static int Run(GeneratorOptions options, CancellationToken ct)
    {
        Stopwatch clock = Stopwatch.StartNew();
        long nextReportMilliseconds = ProgressIntervalMilliseconds;

        long written;
        try
        {
            written = StagedOutput.Write(options, ReportProgress, ct);
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Output file '{options.OutputPath}' could not be written: {ex.Message}");
            return ExitCodes.IoFailure;
        }

        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Wrote {ByteSize.Describe(written)} to {options.OutputPath} in {clock.Elapsed.TotalSeconds:F1}s."));

        return ExitCodes.Success;

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
