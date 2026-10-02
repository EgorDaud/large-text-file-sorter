using FileSorter.Cli;
using FileSorter.Planning;
using FileSorter.Tests.Support;
using Xunit;
using static FileSorter.Tests.Support.TestTimeouts;

namespace FileSorter.Tests.Integration;

[Collection("Program")]
public sealed class CapacityAndVolumeIntegrationTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    [Trait("Case", "IT-07")]
    public void The_capacity_precheck_against_the_real_temp_volume_reports_sufficient()
    {
        // Tiny input on purpose: the subject is the real probe, not this machine's free space.
        string root = Path.GetPathRoot(Path.GetFullPath(_directory.Path)) ?? _directory.Path;
        DriveInfo drive = new(root);
        long? freeBytes = drive.IsReady ? drive.AvailableFreeSpace : null;

        Assert.SkipUnless(freeBytes is not null, $"Drive '{root}' did not report free space; nothing real to check here.");

        CapacityDecision decision = TempCapacity.Evaluate(inputSizeBytes: 1024, freeBytes);

        Assert.Equal(CapacityOutcome.Sufficient, decision.Outcome);
    }

    [Fact]
    [Trait("Case", "IT-08")]
    public async Task Placing_a_single_run_across_two_genuinely_different_volumes_falls_back_to_a_copy()
    {
        string? secondVolumeRoot = FindWritableSecondVolumeRoot(Path.GetPathRoot(Path.GetFullPath(_directory.Path)));
        Assert.SkipUnless(
            secondVolumeRoot is not null,
            "No second writable fixed volume was found on this machine; the cross-volume placement path cannot be exercised for real here.");

        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");

        string outputDirectory = Path.Combine(secondVolumeRoot!, "FileSorterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory, "out.txt");

        try
        {
            // Budget far above the input, so there is one run and phase two takes the RunPlacement path.
            await File.WriteAllTextAsync(
                inputPath, "30. Cherry\n2. Apple\n100. Banana\n2. Aardvark\n", TestContext.Current.CancellationToken);

            SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
            int exitCode = await SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(outputPath));

            string[] sortedLines = await File.ReadAllLinesAsync(outputPath, TestContext.Current.CancellationToken);
            Assert.Equal(["2. Aardvark", "2. Apple", "100. Banana", "30. Cherry"], sortedLines);

            if (Directory.Exists(tempDirectory))
            {
                Assert.Empty(Directory.GetFiles(tempDirectory, "run-*.tmp"));
            }
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    [Trait("Case", "IT-09")]
    public void The_capacity_precheck_treats_a_UNC_share_as_unknown_instead_of_throwing()
    {
        // DriveInfo rejects a UNC root in its constructor without contacting the host, so no network is touched.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC roots and DriveInfo's drive-letter rule are Windows concepts.");

        const string uncDirectory = @"\\nonexistent-host-for-test\share\sub";

        string stderr = ConsoleCapture.Error(() => VolumeCapacity.Check(inputSizeBytes: 1024, uncDirectory, uncDirectory));

        Assert.Contains("Could not determine free space", stderr);
    }

    private static string? FindWritableSecondVolumeRoot(string? excludedRoot)
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || string.Equals(drive.RootDirectory.FullName, excludedRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string probe = Path.Combine(drive.RootDirectory.FullName, "FileSorterTestsProbe_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(probe);
                Directory.Delete(probe);
                return drive.RootDirectory.FullName;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}
