using FileSorter.Cli;
using FileSorter.Tests.Support;

namespace FileSorter.Tests.Properties;

internal static class PropertyHarness
{
    // Generous for a saturated thread pool, yet short enough that a hung run fails instead of stalling the suite.
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(60);

    public static async Task<byte[]> RunSorterAsync(
        byte[] input, long memoryBudgetBytes, int maxLineLength, CancellationToken ct, int parallelism = 2)
    {
        using TempDirectory directory = new();

        string inputPath = Path.Combine(directory.Path, "in.txt");
        string outputPath = Path.Combine(directory.Path, "out.txt");
        string tempDirectory = Path.Combine(directory.Path, "temp");
        await File.WriteAllBytesAsync(inputPath, input, ct);

        // Channels, not Akka: ActorSystem startup per iteration is slow, and the pipelines are tested to agree elsewhere.
        SorterOptions options = new(
            inputPath, outputPath, tempDirectory, memoryBudgetBytes, maxLineLength, parallelism, Pipeline.Channels);

        int exitCode = await SortCommand.RunAsync(options, ct).WaitAsync(BoundedWait, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"The sorter exited with code {exitCode} for a property-generated input; expected success.");
        }

        return await File.ReadAllBytesAsync(outputPath, ct);
    }
}
