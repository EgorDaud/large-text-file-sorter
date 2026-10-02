using System.Diagnostics.CodeAnalysis;
using FileSorter.Verification;
using Shared;

namespace FileSorter.Cli;

internal sealed record VerifyOptions(string InputPath, string OutputPath, int MaxLineLength)
{
    public static readonly long MaxLineLengthCeiling = Array.MaxLength - OutputVerifier.BaseBufferSize;

    // args[0] is "--verify".
    public static bool TryParse(
        string[] args,
        [NotNullWhen(true)] out VerifyOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        options = null;
        error = null;

        string? inputPath = null;
        string? outputPath = null;
        int maxLineLength = CommandLine.DefaultMaxLineLength;

        for (int i = 1; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "--max-line":
                    if (!Arguments.TryTakeValue(args, ref i, argument, out string? maxLine, out error))
                    {
                        return false;
                    }

                    if (!CommandLine.TryParseMaxLine(maxLine, MaxLineLengthCeiling, out maxLineLength, out error))
                    {
                        return false;
                    }

                    break;

                default:
                    if (!CommandLine.TryTakePath(argument, ref inputPath, ref outputPath, out error))
                    {
                        return false;
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            error = "--verify requires an input path.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            error = "--verify requires an output path.";
            return false;
        }

        options = new VerifyOptions(inputPath, outputPath, maxLineLength);
        return true;
    }
}
