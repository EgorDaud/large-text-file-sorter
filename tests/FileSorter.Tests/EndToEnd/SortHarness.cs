using System.Globalization;
using FileSorter.Cli;
using FileSorter.Tests.Support;
using TestFileGenerator.Generation;
using Xunit;

namespace FileSorter.Tests.EndToEnd;

internal static class SortHarness
{
    internal static Task<(int ExitCode, string Stderr)> RunCapturedAsync(SorterOptions options) =>
        ConsoleCapture.ErrorAsync(() => SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeouts.BoundedWait, TestContext.Current.CancellationToken));

    // For failures whose exit code ConsoleRun decides; SortCommand.RunAsync lets those propagate.
    internal static Task<(int ExitCode, string Stderr)> RunThroughConsoleRunCapturedAsync(SorterOptions options) =>
        ConsoleCapture.ErrorAsync(() => Task.Run(() => ConsoleRun.Run(ct => SortCommand.RunAsync(options, ct)))
            .WaitAsync(TestTimeouts.BoundedWait, TestContext.Current.CancellationToken));

    // Unlike the merge-pass line, this one is not gated by the progress timer, so fast sorts still print it.
    internal static int ParseRunCount(string stderr)
    {
        const string marker = "phase one produced ";
        int start = stderr.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected a \"{marker}\" line in stderr, got:\n{stderr}");
        start += marker.Length;
        int end = stderr.IndexOf(' ', start);
        return int.Parse(stderr[start..end], CultureInfo.InvariantCulture);
    }

    internal static void WriteGeneratedInput(string path, long targetBytes, int seed)
    {
        GeneratorOptions options = new(path, targetBytes, seed, DuplicateRatio: 0.2);
        FileWriter writer = new(new LineComposer(options), targetBytes);
        using FileStream output = new(path, FileMode.Create, FileAccess.Write);
        writer.Write(output, TestContext.Current.CancellationToken);
    }

    internal static void AssertIsOracleSortOfInput(string inputPath, string outputPath) =>
        Assert.Equal(NaiveReferenceSort.Sort(File.ReadAllBytes(inputPath)), File.ReadAllBytes(outputPath));

    internal static void AssertNoLeftoverRunFiles(string tempDirectory)
    {
        if (!Directory.Exists(tempDirectory))
        {
            return;
        }

        // Recursive: run files live in a private subdirectory, not directly in tempDirectory.
        string[] leftovers = Directory.GetFiles(tempDirectory, "run-*.tmp", SearchOption.AllDirectories);
        Assert.Empty(leftovers);

        Assert.Empty(Directory.GetDirectories(tempDirectory));
    }
}
