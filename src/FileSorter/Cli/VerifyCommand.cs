using FileSorter.Verification;
using Shared;

namespace FileSorter.Cli;

internal static class VerifyCommand
{
    internal static int Execute(string[] args)
    {
        if (!VerifyOptions.TryParse(args, out VerifyOptions? options, out string? error))
        {
            return CommandLine.ReportUsageError(error);
        }

        return ConsoleRun.Run(ct => RunAsync(options, ct));
    }

    internal static async Task<int> RunAsync(VerifyOptions options, CancellationToken ct)
    {
        Preflight.ValidateReadableFile(options.InputPath, "Input");
        Preflight.ValidateReadableFile(options.OutputPath, "Output");

        VerificationResult result = await OutputVerifier.RunAsync(options.InputPath, options.OutputPath, options.MaxLineLength, ct);

        Console.Error.WriteLine(
            $"  input:  {result.Input.LineCount} lines, {ByteSize.Describe(result.Input.ByteCount)}, hash {result.Input.Hash:x16}");

        if (result.OrderViolationLineNumber is { } violationLine)
        {
            Console.Error.WriteLine(
                $"  output: scan stopped at line {violationLine} ({ByteSize.Describe(result.Output.ByteCount)} total; " +
                "line count and hash not computed past that point) -- see below");
        }
        else
        {
            Console.Error.WriteLine(
                $"  output: {result.Output.LineCount} lines, {ByteSize.Describe(result.Output.ByteCount)}, hash {result.Output.Hash:x16}");
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
