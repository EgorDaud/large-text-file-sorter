using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using FileSorter.Verification;

namespace FileSorter.Startup;

// Parses sort and --verify options. Program prints usage when parsing fails.
internal static class CommandLine
{
    private const long DefaultMemoryBudgetBytes = 1L << 30;   // 1 GiB
    private const int  DefaultMaxLineLength     = 64 * 1024;  // 64 KiB

    // Leave headroom for MemoryBudget's small buffer-floor offsets.
    internal const long MaxLineLengthCeiling = int.MaxValue - 16;

    // Verify allocates BaseBufferSize + maxLineLength per file, so its ceiling must
    // remain below Array.MaxLength. Derive it from OutputVerifier's buffer size.
    internal static readonly long VerifyMaxLineLengthCeiling = Array.MaxLength - OutputVerifier.BaseBufferSize;

    internal const string Usage = """
        Usage:
          sorter <input> <output> [--temp DIR] [--memory 1GiB] [--max-line 64KiB]
                                   [--parallelism N] [--pipeline akka|channels]
          sorter --verify <input> <output> [--max-line 64KiB]

        Options:
          --temp         Directory for temporary run and merge files. Default: the output file's directory.
          --memory       Memory budget: a byte count, optionally suffixed B, KiB, MiB or GiB. Default 1GiB.
          --max-line     Maximum line length: a byte count, optionally suffixed B, KiB, MiB or GiB. Default 64KiB, maximum 2147483631 (int.MaxValue - 16).
          --parallelism  Degree of parallelism for phase one (run generation). Default: the processor count.
          --pipeline     Run-generation strategy, 'akka' or 'channels'. Default akka.

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
          130  cancelled by the operator (Ctrl+C)
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
        Pipeline pipeline = Pipeline.Akka;

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

                    if (!TryParseSize(maxLine, out long parsedMaxLine) || parsedMaxLine <= 0 || parsedMaxLine > MaxLineLengthCeiling)
                    {
                        error = $"--max-line expects a byte count between 1 and {MaxLineLengthCeiling}, optionally suffixed B, KiB, MiB or GiB, but got '{maxLine}'.";
                        return false;
                    }

                    maxLineLength = (int)parsedMaxLine;
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
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"Unknown option '{argument}'.";
                        return false;
                    }

                    if (inputPath is null)
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

        if (string.IsNullOrEmpty(tempDirectory))
        {
            tempDirectory = CapacityProbe.DirectoryOf(outputPath);
        }

        options = new SorterOptions(inputPath, outputPath, tempDirectory, memoryBudgetBytes, maxLineLength, parallelism, pipeline);
        return true;
    }

    // Verify mode shares only --max-line with sorting, so it has a separate parser.
    internal static bool TryParseVerifyOptions(
        string[] args,
        [NotNullWhen(true)] out VerifyOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        options = null;
        error = null;

        string? inputPath = null;
        string? outputPath = null;
        int maxLineLength = DefaultMaxLineLength;

        for (int i = 1; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "--max-line":
                    if (!TryTakeValue(args, ref i, argument, out string? maxLine, out error))
                    {
                        return false;
                    }

                    // Verify mode's fixed per-file buffer has a lower ceiling.
                    if (!TryParseSize(maxLine, out long parsedMaxLine) || parsedMaxLine <= 0 || parsedMaxLine > VerifyMaxLineLengthCeiling)
                    {
                        error = $"--max-line expects a byte count between 1 and {VerifyMaxLineLengthCeiling}, optionally suffixed B, KiB, MiB or GiB, but got '{maxLine}'.";
                        return false;
                    }

                    maxLineLength = (int)parsedMaxLine;
                    break;

                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"Unknown option '{argument}'.";
                        return false;
                    }

                    if (inputPath is null)
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

    private static bool TryTakeValue(
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
