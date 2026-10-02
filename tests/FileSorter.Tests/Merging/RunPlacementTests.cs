using FileSorter.Infrastructure;
using FileSorter.Merging;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.Merging;

// RunPlacement operates on file paths by nature — the seam it exists behind is tryMove,
// not the file system — so these tests use a real scratch directory. No test arranges a
// genuine second volume; the copy fallback is forced by a tryMove of (_, _) => false. In the
// "Program" collection because KM-17 and KM-22 capture the process-wide Console.Error that
// the sort tests also swap.
[Collection("Program")]
public sealed class RunPlacementTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    private readonly TemporaryRunSet _runs;

    public RunPlacementTests() => _runs = new TemporaryRunSet(_directory.Path);

    public void Dispose()
    {
        _runs.Dispose();
        _directory.Dispose();
    }

    [Fact]
    [Trait("Case", "KM-12")]
    public void A_successful_move_places_the_run_without_copying_it()
    {
        string runPath = Path.Combine(_directory.Path, "run.tmp");
        string outputPath = Path.Combine(_directory.Path, "output.tmp");
        byte[] bytes = [1, 2, 3, 4, 5];
        File.WriteAllBytes(runPath, bytes);

        // The default tryMove performs the real move; Place is then only required to
        // return. If it went on to copy runPath afterwards, this would throw,
        // because the move has already made runPath disappear.
        RunPlacement.Place(runPath, outputPath, _runs);

        Assert.Equal(bytes, File.ReadAllBytes(outputPath));
        Assert.False(File.Exists(runPath));
    }

    [Fact]
    [Trait("Case", "KM-13")]
    public void A_failed_move_falls_back_to_a_copy_and_the_caller_never_sees_an_error()
    {
        string runPath = Path.Combine(_directory.Path, "run.tmp");
        string outputPath = Path.Combine(_directory.Path, "output.tmp");
        byte[] bytes = [9, 8, 7];
        File.WriteAllBytes(runPath, bytes);

        Exception? thrown = Record.Exception(() =>
            RunPlacement.Place(runPath, outputPath, _runs, (_, _) => false));

        Assert.Null(thrown);
        Assert.Equal(bytes, File.ReadAllBytes(outputPath));
    }

    [Fact]
    [Trait("Case", "KM-17")]
    public void A_run_file_cleanup_failure_after_a_successful_place_leaves_the_replaced_destination_intact()
    {
        // Windows-only reproduction: FileShare.Read alone permits the concurrent read
        // Place's own File.Copy performs, but denies the delete that follows once the
        // destination has already been replaced. That delete is best-effort, so Place
        // neither throws nor leaves the destination partial.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FileShare-based delete denial is a Windows sharing-mode concept.");

        string runPath = Path.Combine(_directory.Path, "run.tmp");
        string outputPath = Path.Combine(_directory.Path, "output.tmp");
        byte[] bytes = [1, 2, 3, 4, 5];
        File.WriteAllBytes(runPath, bytes);

        TextWriter originalError = Console.Error;
        Console.SetError(new StringWriter());
        try
        {
            using (new FileStream(runPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception? thrown = Record.Exception(() => RunPlacement.Place(runPath, outputPath, _runs, (_, _) => false));
                Assert.Null(thrown);
            }
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Equal(bytes, File.ReadAllBytes(outputPath));
    }

    [Fact]
    [Trait("Case", "KM-22")]
    public void A_source_run_that_cannot_be_deleted_after_the_copy_fallback_warns_but_neither_fails_nor_leaks_the_run()
    {
        // A read-only attribute blocks File.Delete only on Windows; File.Copy and the
        // final move are unaffected. It stands in for a scanner holding the finished run.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A read-only attribute blocks File.Delete only on Windows.");

        string runPath = _runs.CreateRunPath();
        string outputPath = Path.Combine(_directory.Path, "output.tmp");
        byte[] bytes = [1, 2, 3, 4, 5];
        File.WriteAllBytes(runPath, bytes);
        string? privateDirectory = Path.GetDirectoryName(runPath);
        Assert.NotNull(privateDirectory);

        TextWriter originalError = Console.Error;
        StringWriter capturedError = new();
        File.SetAttributes(runPath, FileAttributes.ReadOnly);
        Console.SetError(capturedError);
        try
        {
            Exception? thrown = Record.Exception(() => RunPlacement.Place(runPath, outputPath, _runs, (_, _) => false));
            Assert.Null(thrown);
        }
        finally
        {
            Console.SetError(originalError);
            File.SetAttributes(runPath, FileAttributes.Normal);
            File.SetAttributes(outputPath, FileAttributes.Normal);
        }

        // The output is complete, and the failed delete was reported by name.
        Assert.Equal(bytes, File.ReadAllBytes(outputPath));
        Assert.Contains(runPath, capturedError.ToString());
        Assert.True(File.Exists(runPath));

        // The run stays tracked, so Dispose retries it once the block is lifted.
        _runs.Dispose();
        Assert.False(File.Exists(runPath));
        Assert.False(Directory.Exists(privateDirectory));
    }

    [Fact]
    [Trait("Case", "KM-19")]
    public void The_copy_fallback_never_leaves_a_staging_file_behind_on_success()
    {
        string runPath = Path.Combine(_directory.Path, "run.tmp");
        string outputPath = Path.Combine(_directory.Path, "output.tmp");
        File.WriteAllBytes(runPath, [1, 2, 3]);

        RunPlacement.Place(runPath, outputPath, _runs, (_, _) => false);

        Assert.Empty(Directory.GetFiles(_directory.Path, "*.partial"));
    }

    [Fact]
    [Trait("Case", "KM-20")]
    public void The_copy_fallback_replaces_an_existing_destination()
    {
        string runPath = Path.Combine(_directory.Path, "run.tmp");
        string outputPath = Path.Combine(_directory.Path, "output.tmp");
        File.WriteAllBytes(outputPath, [0, 0, 0, 0, 0]);
        byte[] newContent = [1, 2, 3];
        File.WriteAllBytes(runPath, newContent);

        RunPlacement.Place(runPath, outputPath, _runs, (_, _) => false);

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

        string runPath = Path.Combine(_directory.Path, "run.tmp");
        string outputPath = Path.Combine(_directory.Path, "output.tmp");
        byte[] previousContent = [9, 9, 9, 9];
        File.WriteAllBytes(outputPath, previousContent);
        File.WriteAllBytes(runPath, [1, 2, 3]);

        using (new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.Throws<UnauthorizedAccessException>(
                () => RunPlacement.Place(runPath, outputPath, _runs, (_, _) => false));
        }

        Assert.Equal(previousContent, File.ReadAllBytes(outputPath));
        Assert.Empty(Directory.GetFiles(_directory.Path, "*.partial"));
    }

    [Fact]
    [Trait("Case", "KM-14")]
    public void No_temporary_file_survives_placement_by_move_or_by_copy()
    {
        string movedRun = Path.Combine(_directory.Path, "moved.tmp");
        string movedOutput = Path.Combine(_directory.Path, "moved-output.tmp");
        File.WriteAllBytes(movedRun, [1]);
        RunPlacement.Place(movedRun, movedOutput, _runs);
        Assert.False(File.Exists(movedRun));

        string copiedRun = Path.Combine(_directory.Path, "copied.tmp");
        string copiedOutput = Path.Combine(_directory.Path, "copied-output.tmp");
        File.WriteAllBytes(copiedRun, [2]);
        RunPlacement.Place(copiedRun, copiedOutput, _runs, (_, _) => false);
        Assert.False(File.Exists(copiedRun));
    }

    [Fact]
    [Trait("Case", "KM-23")]
    public void PlaceEmpty_creates_an_empty_output_or_replaces_an_existing_one_and_leaves_no_staging_file()
    {
        string created = Path.Combine(_directory.Path, "created.tmp");
        RunPlacement.PlaceEmpty(created);
        Assert.True(File.Exists(created));
        Assert.Empty(File.ReadAllBytes(created));

        string replaced = Path.Combine(_directory.Path, "replaced.tmp");
        File.WriteAllBytes(replaced, [9, 9, 9]);
        RunPlacement.PlaceEmpty(replaced);
        Assert.Empty(File.ReadAllBytes(replaced));

        Assert.Empty(Directory.GetFiles(_directory.Path, "*.partial"));
    }

    [Fact]
    [Trait("Case", "KM-24")]
    public void PlaceEmpty_throws_for_an_unreplaceable_destination_and_leaves_it_and_no_staging_file()
    {
        // Same Windows-only write denial as KM-21. Program maps this exception to exit 3.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FileShare-based write denial is a Windows sharing-mode concept.");

        string outputPath = Path.Combine(_directory.Path, "output.tmp");
        byte[] previousContent = [9, 9, 9, 9];
        File.WriteAllBytes(outputPath, previousContent);

        using (new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.Throws<UnauthorizedAccessException>(() => RunPlacement.PlaceEmpty(outputPath));
        }

        Assert.Equal(previousContent, File.ReadAllBytes(outputPath));
        Assert.Empty(Directory.GetFiles(_directory.Path, "*.partial"));
    }
}
