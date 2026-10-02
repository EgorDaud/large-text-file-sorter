using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Shared;
using TestFileGenerator.Generation;

namespace TestFileGenerator;

internal static class CommandLine
{
    internal const string Usage = """
        Usage:
          generator <output> --size 100GiB [--seed 42] [--duplicate-ratio 0.1]

        Options:
          --size             Target file size: a byte count, optionally suffixed B, KiB, MiB or GiB. Required.
          --seed             Seed for the random source. The same seed reproduces byte-identical output on the same .NET version. Default 0.
          --duplicate-ratio  Proportion of lines reusing a string part, between 0 and 1. Default 0.1.

        Complete lines are written until the next one would exceed the target, so the file
        is never larger than asked for and never ends in a partial line.
        """;

    internal static bool TryParseOptions(
        string[] args,
        [NotNullWhen(true)] out GeneratorOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        options = null;
        error = null;

        string? outputPath = null;
        long? targetBytes = null;
        int seed = 0;
        double duplicateRatio = GeneratorOptions.DefaultDuplicateRatio;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "--size":
                    if (!Arguments.TryTakeValue(args, ref i, argument, out string? size, out error))
                    {
                        return false;
                    }

                    if (!ByteSize.TryParse(size, out long parsedSize))
                    {
                        error = $"--size expects a byte count, optionally suffixed B, KiB, MiB or GiB, but got '{size}'.";
                        return false;
                    }

                    targetBytes = parsedSize;
                    break;

                case "--seed":
                    if (!Arguments.TryTakeValue(args, ref i, argument, out string? seedText, out error))
                    {
                        return false;
                    }

                    if (!int.TryParse(seedText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seed))
                    {
                        error = $"--seed expects an integer, but got '{seedText}'.";
                        return false;
                    }

                    break;

                case "--duplicate-ratio":
                    if (!Arguments.TryTakeValue(args, ref i, argument, out string? ratioText, out error))
                    {
                        return false;
                    }

                    // Pattern matching also rejects NaN.
                    if (!double.TryParse(ratioText, NumberStyles.Float, CultureInfo.InvariantCulture, out duplicateRatio)
                        || duplicateRatio is not (>= 0 and <= 1))
                    {
                        error = $"--duplicate-ratio expects a value between 0 and 1, but got '{ratioText}'.";
                        return false;
                    }

                    break;

                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"Unknown option '{argument}'.";
                        return false;
                    }

                    if (outputPath is not null)
                    {
                        error = $"Unexpected argument '{argument}'; the output path is already '{outputPath}'.";
                        return false;
                    }

                    outputPath = argument;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            error = "An output path is required.";
            return false;
        }

        if (targetBytes is null)
        {
            error = "--size is required.";
            return false;
        }

        options = new GeneratorOptions(outputPath, targetBytes.Value, seed, duplicateRatio);
        return true;
    }
}
