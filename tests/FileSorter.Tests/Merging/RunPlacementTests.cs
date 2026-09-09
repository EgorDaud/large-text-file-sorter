using FileSorter.Merging;
using Xunit;

namespace FileSorter.Tests.Merging;

// RunPlacement operates on file paths by nature — the seam it exists behind is tryMove,
// not the file system — so these tests use a real scratch directory. No test arranges a
// genuine second volume; the copy fallback is forced by a tryMove of (_, _) => false.
public sealed class RunPlacementTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "FileSorterTests", Guid.NewGuid().ToString("N"));

    public RunPlacementTests()
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
    [Trait("Case", "KM-12")]
    public void A_successful_move_places_the_run_without_copying_it()
    {
        string runPath = Path.Combine(_directory, "run.tmp");
        string outputPath = Path.Combine(_directory, "output.tmp");
        byte[] bytes = [1, 2, 3, 4, 5];
        File.WriteAllBytes(runPath, bytes);

        // tryMove performs the real move itself; Place is then only required to
        // return. If it went on to copy runPath afterwards, this would throw,
        // because the move has already made runPath disappear.
        RunPlacement.Place(runPath, outputPath, (from, to) =>
        {
            File.Move(from, to);
            return true;
        });

        Assert.Equal(bytes, File.ReadAllBytes(outputPath));
        Assert.False(File.Exists(runPath));
    }

    [Fact]
    [Trait("Case", "KM-13")]
    public void A_failed_move_falls_back_to_a_copy_and_the_caller_never_sees_an_error()
    {
        string runPath = Path.Combine(_directory, "run.tmp");
        string outputPath = Path.Combine(_directory, "output.tmp");
        byte[] bytes = [9, 8, 7];
        File.WriteAllBytes(runPath, bytes);

        Exception? thrown = Record.Exception(() =>
            RunPlacement.Place(runPath, outputPath, (_, _) => false));

        Assert.Null(thrown);
        Assert.Equal(bytes, File.ReadAllBytes(outputPath));
    }

    [Fact]
    [Trait("Case", "KM-17")]
    public void A_run_file_cleanup_failure_after_a_successful_place_leaves_the_replaced_destination_intact()
    {
        // Windows-only reproduction: FileShare.Read alone permits the concurrent read
        // Place's own File.Copy performs, but denies the delete that follows once the
        // destination has already been replaced. Place reports nothing about whether
        // that replace happened, so a caller cannot mistake this failure -- which
        // leaves a fully correct destination behind -- for one that leaves it partial.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FileShare-based delete denial is a Windows sharing-mode concept.");

        string runPath = Path.Combine(_directory, "run.tmp");
        string outputPath = Path.Combine(_directory, "output.tmp");
        byte[] bytes = [1, 2, 3, 4, 5];
        File.WriteAllBytes(runPath, bytes);

        using (new FileStream(runPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => RunPlacement.Place(runPath, outputPath, (_, _) => false));
        }

        Assert.Equal(bytes, File.ReadAllBytes(outputPath));
    }

    [Fact]
    [Trait("Case", "KM-19")]
    public void The_copy_fallback_never_leaves_a_staging_file_behind_on_success()
    {
        string runPath = Path.Combine(_directory, "run.tmp");
        string outputPath = Path.Combine(_directory, "output.tmp");
        File.WriteAllBytes(runPath, [1, 2, 3]);

        RunPlacement.Place(runPath, outputPath, (_, _) => false);

        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }

    [Fact]
    [Trait("Case", "KM-20")]
    public void The_copy_fallback_replaces_an_existing_destination()
    {
        string runPath = Path.Combine(_directory, "run.tmp");
        string outputPath = Path.Combine(_directory, "output.tmp");
        File.WriteAllBytes(outputPath, [0, 0, 0, 0, 0]);
        byte[] newContent = [1, 2, 3];
        File.WriteAllBytes(runPath, newContent);

        RunPlacement.Place(runPath, outputPath, (_, _) => false);

        Assert.Equal(newContent, File.ReadAllBytes(outputPath));
    }

    [Fact]
    [Trait("Case", "KM-21")]
    public void A_failed_final_move_in_the_copy_fallback_leaves_the_existing_destination_untouched_and_deletes_only_the_staging_file()
    {
        // Windows-only reproduction: a destination held open for read with delete
        // sharing denies the write access File.Move needs to replace it, without
        // denying the delete a plain rename would use. A Unix rename does not
        // distinguish these cases, so nothing here would fail on Linux the same way.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FileShare-based write denial is a Windows sharing-mode concept.");

        string runPath = Path.Combine(_directory, "run.tmp");
        string outputPath = Path.Combine(_directory, "output.tmp");
        byte[] previousContent = [9, 9, 9, 9];
        File.WriteAllBytes(outputPath, previousContent);
        File.WriteAllBytes(runPath, [1, 2, 3]);

        using (new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.Throws<UnauthorizedAccessException>(
                () => RunPlacement.Place(runPath, outputPath, (_, _) => false));
        }

        Assert.Equal(previousContent, File.ReadAllBytes(outputPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }

    [Fact]
    [Trait("Case", "KM-14")]
    public void No_temporary_file_survives_placement_by_move_or_by_copy()
    {
        string movedRun = Path.Combine(_directory, "moved.tmp");
        string movedOutput = Path.Combine(_directory, "moved-output.tmp");
        File.WriteAllBytes(movedRun, [1]);
        RunPlacement.Place(movedRun, movedOutput, (from, to) => { File.Move(from, to); return true; });
        Assert.False(File.Exists(movedRun));

        string copiedRun = Path.Combine(_directory, "copied.tmp");
        string copiedOutput = Path.Combine(_directory, "copied-output.tmp");
        File.WriteAllBytes(copiedRun, [2]);
        RunPlacement.Place(copiedRun, copiedOutput, (_, _) => false);
        Assert.False(File.Exists(copiedRun));
    }
}
