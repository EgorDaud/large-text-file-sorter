using System.Diagnostics.CodeAnalysis;
using FileSorter.Cli;

namespace FileSorter.Verification;

// Verification uses fixed scan buffers; maxLineLength bounds carry between reads.
internal sealed record VerifyOptions(string InputPath, string OutputPath, int MaxLineLength)
{
    // Verify allocates OutputVerifier.BaseBufferSize + maxLineLength per file, so its ceiling
    // must remain below Array.MaxLength.
    internal static readonly long MaxLineLengthCeiling = Array.MaxLength - OutputVerifier.BaseBufferSize;

    // Verify mode shares only --max-line with sorting, so it has its own parser. args[0] is
    // "--verify". Program prints usage when parsing fails.
    internal static bool TryParse(
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
                    if (!CommandLine.TryTakeValue(args, ref i, argument, out string? maxLine, out error))
                    {
                        return false;
                    }

                    // Verify mode's fixed per-file buffer has a lower ceiling.
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
