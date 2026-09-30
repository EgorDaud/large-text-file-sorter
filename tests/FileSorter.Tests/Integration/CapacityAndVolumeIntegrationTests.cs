using FileSorter.Startup;
using Xunit;

namespace FileSorter.Tests.Integration;

/// <summary>
/// What remains of the startup-capacity and single-run-placement behaviour once the
/// decisions themselves are behind seams and unit tested: each case below exercises the
/// real operating system rather than an injected number or a stubbed delegate.
/// </summary>
[Collection("Program")]
public sealed class CapacityAndVolumeIntegrationTests : IDisposable
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "FileSorterTests", Guid.NewGuid().ToString("N"));

    public CapacityAndVolumeIntegrationTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Case", "IT-07")]
    public void The_capacity_precheck_against_the_real_temp_volume_reports_sufficient()
    {
        // TempCapacity.Evaluate is unit tested against invented numbers; what those
        // cases cannot show is that a real DriveInfo probe on the machine running the
        // suite feeds Evaluate a figure it reports Sufficient for. The trivially small
        // input size is deliberate: the probe is the subject, not how much free space
        // this machine happens to have.
        string root = Path.GetPathRoot(Path.GetFullPath(_directory)) ?? _directory;
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
        // The copy fallback is unit tested with an injected tryMove that reports
        // failure, which proves the fallback logic but not that File.Move genuinely
        // throws across two real volumes the way Program's tryMove closure assumes.
        // Without a second volume this case skips; it must never silently pass.
        string? secondVolumeRoot = FindWritableSecondVolumeRoot(Path.GetPathRoot(Path.GetFullPath(_directory)));
        Assert.SkipUnless(
            secondVolumeRoot is not null,
            "No second writable fixed volume was found on this machine; the cross-volume placement path cannot be exercised for real here.");

        string inputPath = Path.Combine(_directory, "in.txt");
        string tempDirectory = Path.Combine(_directory, "temp");

        string outputDirectory = Path.Combine(secondVolumeRoot!, "FileSorterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory, "out.txt");

        try
        {
            // A handful of lines against a budget far larger than they need, so phase
            // one emits exactly one run and phase two takes the single-run RunPlacement
            // path rather than any merge.
            await File.WriteAllTextAsync(
                inputPath, "30. Cherry\n2. Apple\n100. Banana\n2. Aardvark\n", TestContext.Current.CancellationToken);

            SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
            int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(outputPath));

            string[] sortedLines = await File.ReadAllLinesAsync(outputPath, TestContext.Current.CancellationToken);
            Assert.Equal(["2. Aardvark", "2. Apple", "100. Banana", "30. Cherry"], sortedLines);

            // The copy fallback must not leave its source run behind, checked here
            // against a real cross-volume copy.
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
        // DriveInfo rejects any root that is not a drive letter with ArgumentException,
        // and does so in the constructor without contacting the host, so this host name
        // needs no network access and cannot stall on a timeout. The directory need not
        // exist: neither the probe nor the same-volume comparison touches it.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC roots and DriveInfo's drive-letter rule are Windows concepts.");

        const string uncDirectory = @"\\nonexistent-host-for-test\share\sub";

        TextWriter originalError = Console.Error;
        StringWriter capturedError = new();
        int? exitCode;
        Console.SetError(capturedError);
        try
        {
            exitCode = CapacityProbe.CheckCapacity(inputSizeBytes: 1024, uncDirectory, uncDirectory);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Null(exitCode);
        Assert.Contains("Could not determine free space", capturedError.ToString());
    }

    // A volume other than excludedRoot that is ready and actually writable by this
    // process, confirmed by creating and removing a probe directory rather than trusted
    // from DriveInfo.DriveType alone.
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
