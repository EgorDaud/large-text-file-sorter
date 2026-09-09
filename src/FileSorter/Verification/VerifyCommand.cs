using FileSorter.LineFormat;
using FileSorter.Startup;

namespace FileSorter.Verification;

// Runs verification and presents its result as a CLI exit code and diagnostics.
internal static class VerifyCommand
{
    internal static int Execute(string[] args)
    {
        if (!CommandLine.TryParseVerifyOptions(args, out VerifyOptions? options, out string? error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLine.Usage);
            return ExitCodes.InvalidArguments;
        }

        using CancellationTokenSource cts = new();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            return RunAsync(options, cts.Token).GetAwaiter().GetResult();
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
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    // Shared entry point for the CLI and end-to-end tests.
    internal static async Task<int> RunAsync(VerifyOptions options, CancellationToken ct)
    {
        if (!File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file not found: '{options.InputPath}'.");
            return ExitCodes.InvalidArguments;
        }

        if (!File.Exists(options.OutputPath))
        {
            Console.Error.WriteLine($"Output file not found: '{options.OutputPath}'.");
            return ExitCodes.InvalidArguments;
        }

        VerificationResult result = await OutputVerifier.RunAsync(options, ct);

        Console.Error.WriteLine(
            $"  input:  {result.Input.LineCount} lines, {ProgressReporter.Describe(result.Input.ByteCount)}, hash {result.Input.Hash:x16}");

        // An order violation leaves the count and hash partial. Only the file size is final.
        if (result.OrderViolationLineNumber is { } violationLine)
        {
            Console.Error.WriteLine(
                $"  output: scan stopped at line {violationLine} ({ProgressReporter.Describe(result.Output.ByteCount)} total; " +
                "line count and hash not computed past that point) -- see below");
        }
        else
        {
            Console.Error.WriteLine(
                $"  output: {result.Output.LineCount} lines, {ProgressReporter.Describe(result.Output.ByteCount)}, hash {result.Output.Hash:x16}");
        }

        if (result.Outcome == VerificationOutcome.Verified)
        {
            Console.Error.WriteLine("verified");
            return ExitCodes.Success;
        }

        Console.Error.WriteLine(result.FailureDetail);
        return ExitCodes.VerificationFailed;
    }
}
