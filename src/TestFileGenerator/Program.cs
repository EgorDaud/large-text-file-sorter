using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using TestFileGenerator.Generation;

namespace TestFileGenerator;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitInvalidArguments = 3;

    private const double DefaultDuplicateRatio = 0.1;

    private const int OutputBufferBytes = 1 << 20;
    private const int ProgressIntervalMilliseconds = 2000;
    private const int MaxStagingAttempts = 8;

    private const string Usage = """
        Usage:
          generator <output> --size 100GiB [--seed 42] [--duplicate-ratio 0.1]

        Options:
          --size             Target file size: a byte count, optionally suffixed B, KiB, MiB or GiB. Required.
          --seed             Seed for the random source. The same seed reproduces byte-identical output. Default 0.
          --duplicate-ratio  Proportion of lines reusing a string part, between 0 and 1. Default 0.1.

        Complete lines are written until the next one would exceed the target, so the file
        is never larger than asked for and never ends in a partial line.
        """;

    // Match longer suffixes before B.
    private static readonly (string Suffix, long Multiplier)[] SizeUnits =
    [
        ("KiB", 1L << 10),
        ("MiB", 1L << 20),
        ("GiB", 1L << 30),
        ("B", 1L),
    ];

    // Report only units accepted by the parser.
    private static readonly string[] SizeNames = ["B", "KiB", "MiB", "GiB"];

    private static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.Error.WriteLine(Usage);
            return ExitSuccess;
        }

        if (!TryParseOptions(args, out GeneratorOptions? options, out string? error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return ExitInvalidArguments;
        }

        // Validate composition before touching an existing output.
        LineComposer composer = new(options);

        Stopwatch clock = Stopwatch.StartNew();
        long nextReportMilliseconds = ProgressIntervalMilliseconds;

        long written;
        try
        {
            written = WriteToOutput(options, composer, ReportProgress);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Include the requested path in write failures.
            Console.Error.WriteLine($"Output file '{options.OutputPath}' could not be written: {ex.Message}");
            return ExitInvalidArguments;
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
        double duplicateRatio = DefaultDuplicateRatio;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "--size":
                    if (!TryTakeValue(args, ref i, argument, out string? size, out error))
                    {
                        return false;
                    }

                    if (!TryParseSize(size, out long parsedSize))
                    {
                        error = $"--size expects a byte count, optionally suffixed B, KiB, MiB or GiB, but got '{size}'.";
                        return false;
                    }

                    targetBytes = parsedSize;
                    break;

                case "--seed":
                    if (!TryTakeValue(args, ref i, argument, out string? seedText, out error))
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
                    if (!TryTakeValue(args, ref i, argument, out string? ratioText, out error))
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

    // Write beside the output and replace it only after a successful write.
    internal static long WriteToOutput(GeneratorOptions options, LineComposer composer, Action<long> reportProgress)
    {
        string stagingPath = CreateStagingPath(options.OutputPath);
        try
        {
            long written;
            using (FileStream output = new(
                stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                OutputBufferBytes, FileOptions.SequentialScan))
            {
                written = FileWriter.Write(output, composer, options.TargetBytes, reportProgress);
            }

            File.Move(stagingPath, options.OutputPath, overwrite: true);
            return written;
        }
        catch
        {
            // A staging file can look like a smaller valid output, so remove it.
            DeleteStagingFile(stagingPath);
            throw;
        }
    }

    // This duplicates Merging/StagingFile because the two executables share no project.
    // Keep its retry and cleanup behavior aligned with the sorter.
    private static string CreateStagingPath(string outputPath)
    {
        for (int attempt = 0; attempt < MaxStagingAttempts; attempt++)
        {
            string candidate = $"{outputPath}.{RandomStagingSuffix()}.partial";
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException(
            $"Could not choose a staging file name beside '{outputPath}' after {MaxStagingAttempts} attempts.");
    }

    private static string RandomStagingSuffix()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // Cleanup failures must not hide the original write failure.
    private static void DeleteStagingFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception removal) when (removal is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not remove the staging file at {path}: {removal.Message}");
        }
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
