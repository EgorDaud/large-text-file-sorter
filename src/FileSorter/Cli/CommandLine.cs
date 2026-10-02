using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace FileSorter.Cli;

// Parses sort-mode options and the size, path and --max-line syntax VerifyOptions shares. Program prints usage
// when parsing fails.
internal static class CommandLine
{
    private const long DefaultMemoryBudgetBytes = 1L << 30;   // 1 GiB
    internal const int DefaultMaxLineLength      = 64 * 1024;  // 64 KiB

    // A chunk must exceed maxLine + 2 bytes yet fit one array, so no budget can satisfy a larger value.
    internal static readonly long MaxLineLengthCeiling = Array.MaxLength - 3;

    internal static readonly string Usage = $"""
        Usage:
          sorter <input> <output> [--temp DIR] [--memory 1GiB] [--max-line 64KiB]
                                   [--parallelism N] [--pipeline akka|channels]
          sorter --verify <input> <output> [--max-line 64KiB]

        Options:
          --temp         Directory for temporary run and merge files. Default: the output file's directory.
          --memory       Memory budget: a byte count, optionally suffixed B, KiB, MiB or GiB. Default 1GiB.
          --max-line     Maximum line length: a byte count, optionally suffixed B, KiB, MiB or GiB. Default 64KiB, maximum {MaxLineLengthCeiling} (Array.MaxLength - 3).
          --parallelism  Degree of parallelism for phase one (run generation). Default: the processor count.
          --pipeline     Run-generation strategy, 'akka' or 'channels'. Default channels.

        Verify mode:
          --verify       Checks that <output> is a valid sort of <input> under the sorter's own order,
                         without a second full sort to compare against. Reads each file once, in
                         parallel, requiring the output's adjacent lines to be non-decreasing and both
                         files to agree on line count and an order-independent per-line hash. Prints one
                         summary line per file (lines, bytes, hash), then "verified" or the first failure.
                         Honours --max-line, at a lower maximum than sort mode's (--verify reads each
                         file through one fixed-size buffer rather than a --memory-derived plan); no
                         --memory, --parallelism or --pipeline.

        Exit codes:
          0    success (or verified, in --verify mode)
          1    malformed input line (byte offset, line number and a preview on stderr)
          2    insufficient space on the temp or output volume (required, available and the directory examined, on stderr)
          3    invalid arguments; usage printed
          4    --verify found the output out of order, missing its final terminator, or disagreeing with the input's line count or hash
          5    I/O failure after startup validation passed, such as a full disk (one line on stderr)
          130  cancelled by the operator (Ctrl+C or SIGTERM)
        """;

    // Keep "B" last so it cannot shadow longer suffixes.
    private static readonly (string Suffix, long Multiplier)[] SizeUnits =
    [
        ("KiB", 1L << 10),
        ("MiB", 1L << 20),
        ("GiB", 1L << 30),
        ("B", 1L),
    ];

    internal static bool TryParseOptions(
        string[] args,
        [NotNullWhen(true)] out SorterOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        options = null;
        error = null;

        string? inputPath = null;
        string? outputPath = null;
        string? tempDirectory = null;
        long memoryBudgetBytes = DefaultMemoryBudgetBytes;
        int maxLineLength = DefaultMaxLineLength;
        int parallelism = Environment.ProcessorCount;
        Pipeline pipeline = Pipeline.Channels;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "--temp":
                    if (!TryTakeValue(args, ref i, argument, out string? temp, out error))
                    {
                        return false;
                    }

                    tempDirectory = temp;
                    break;

                case "--memory":
                    if (!TryTakeValue(args, ref i, argument, out string? memory, out error))
                    {
                        return false;
                    }

                    if (!TryParseSize(memory, out long parsedMemory) || parsedMemory <= 0)
                    {
                        error = $"--memory expects a positive byte count, optionally suffixed B, KiB, MiB or GiB, but got '{memory}'.";
                        return false;
                    }

                    memoryBudgetBytes = parsedMemory;
                    break;

                case "--max-line":
                    if (!TryTakeValue(args, ref i, argument, out string? maxLine, out error))
                    {
                        return false;
                    }

                    if (!TryParseMaxLine(maxLine, MaxLineLengthCeiling, out maxLineLength, out error))
                    {
                        return false;
                    }

                    break;

                case "--parallelism":
                    if (!TryTakeValue(args, ref i, argument, out string? parallelismText, out error))
                    {
                        return false;
                    }

                    if (!int.TryParse(parallelismText, NumberStyles.None, CultureInfo.InvariantCulture, out parallelism)
                        || parallelism <= 0)
                    {
                        error = $"--parallelism expects a positive integer, but got '{parallelismText}'.";
                        return false;
                    }

                    break;

                case "--pipeline":
                    if (!TryTakeValue(args, ref i, argument, out string? pipelineText, out error))
                    {
                        return false;
                    }

                    if (string.Equals(pipelineText, "akka", StringComparison.OrdinalIgnoreCase))
                    {
                        pipeline = Pipeline.Akka;
                    }
                    else if (string.Equals(pipelineText, "channels", StringComparison.OrdinalIgnoreCase))
                    {
                        pipeline = Pipeline.Channels;
                    }
                    else
                    {
                        error = $"--pipeline expects 'akka' or 'channels', but got '{pipelineText}'.";
                        return false;
                    }

                    break;

                default:
                    if (!TryTakePath(argument, ref inputPath, ref outputPath, out error))
                    {
                        return false;
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            error = "An input path is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            error = "An output path is required.";
            return false;
        }

        // The merge opens the output with FileMode.Create, which would truncate the input.
        if (RefersToSameFile(inputPath, outputPath))
        {
            error = "Input and output must be different files.";
            return false;
        }

        if (string.IsNullOrEmpty(tempDirectory))
        {
            tempDirectory = CapacityProbe.DirectoryOf(outputPath);
        }

        options = new SorterOptions(inputPath, outputPath, tempDirectory, memoryBudgetBytes, maxLineLength, parallelism, pipeline);
        return true;
    }

    // Windows and macOS volumes are case-insensitive by default. An unresolvable path is
    // not a match, so the code that opens it keeps reporting it as before.
    private static bool RefersToSameFile(string inputPath, string outputPath)
    {
        try
        {
            StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    internal static bool TryTakeValue(
        string[] args,
        ref int index,
        string option,
        [NotNullWhen(true)] out string? value,
        [NotNullWhen(false)] out string? error)
    {
        if (index + 1 >= args.Length)
        {
            value = null;
            error = $"{option} expects a value.";
            return false;
        }

        value = args[++index];
        error = null;
        return true;
    }

    // Takes a bare argument as the input path, then the output path. Both modes share it, so
    // they reject unknown options and extra paths alike.
    internal static bool TryTakePath(
        string argument,
        ref string? inputPath,
        ref string? outputPath,
        [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            error = $"Unknown option '{argument}'.";
        }
        else if (inputPath is null)
        {
            inputPath = argument;
        }
        else if (outputPath is null)
        {
            outputPath = argument;
        }
        else
        {
            error = $"Unexpected argument '{argument}'; input and output are already '{inputPath}' and '{outputPath}'.";
        }

        return error is null;
    }

    // Sort and verify mode differ only in the ceiling.
    internal static bool TryParseMaxLine(
        string text,
        long ceiling,
        out int maxLineLength,
        [NotNullWhen(false)] out string? error)
    {
        maxLineLength = 0;
        error = null;
        if (!TryParseSize(text, out long bytes) || bytes <= 0 || bytes > ceiling)
        {
            error = $"--max-line expects a byte count between 1 and {ceiling}, optionally suffixed B, KiB, MiB or GiB, but got '{text}'.";
            return false;
        }

        maxLineLength = (int)bytes;
        return true;
    }

    private static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        long multiplier = 1;
        ReadOnlySpan<char> digits = text;

        foreach ((string suffix, long unit) in SizeUnits)
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                multiplier = unit;
                digits = text.AsSpan(0, text.Length - suffix.Length);
                break;
            }
        }

        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            || value > long.MaxValue / multiplier)
        {
            return false;
        }

        bytes = value * multiplier;
        return true;
    }
}
