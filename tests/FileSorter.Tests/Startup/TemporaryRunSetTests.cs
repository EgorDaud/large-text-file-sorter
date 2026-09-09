using FileSorter.Startup;
using Xunit;

namespace FileSorter.Tests.Startup;

// The one type in this slice that is allowed to touch the real file system: it exists
// specifically to own temp files, so its tests use a real scratch directory rather than
// a MemoryStream. Every test creates and removes that directory itself, so the suite
// leaves nothing behind regardless of what TemporaryRunSet's own cleanup does.
public sealed class TemporaryRunSetTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "FileSorterTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Case", "TR-01")]
    public void Creating_a_run_path_places_it_inside_a_private_directory_beneath_the_temp_directory()
    {
        using TemporaryRunSet runs = new(_directory);

        string path = runs.CreateRunPath();
        string? runDirectory = Path.GetDirectoryName(path);

        Assert.NotEqual(_directory, runDirectory);
        Assert.NotNull(runDirectory);
        Assert.Equal(_directory, Path.GetDirectoryName(runDirectory));
        Assert.True(Directory.Exists(runDirectory));
    }

    [Fact]
    [Trait("Case", "TR-02")]
    public void Two_instances_sharing_the_same_temp_parent_use_different_private_directories()
    {
        // Two invocations sharing --temp must never be able to name the same run
        // file, even by coincidence of both starting their own counter at 1.
        using TemporaryRunSet first = new(_directory);
        using TemporaryRunSet second = new(_directory);

        string firstPath = first.CreateRunPath();
        string secondPath = second.CreateRunPath();

        Assert.NotEqual(Path.GetDirectoryName(firstPath), Path.GetDirectoryName(secondPath));
        Assert.NotEqual(firstPath, secondPath);
    }

    [Fact]
    [Trait("Case", "TR-03")]
    public void Constructing_the_set_does_not_yet_create_a_private_directory()
    {
        // The private directory is created on the first CreateRunPath call, not in
        // the constructor: Program relies on that ordering to keep this invocation's
        // own namespace off disk until the disk-capacity precheck has passed.
        using TemporaryRunSet runs = new(_directory);

        Assert.Empty(Directory.GetDirectories(_directory));
    }

    [Fact]
    public void Creating_two_run_paths_never_collide()
    {
        using TemporaryRunSet runs = new(_directory);

        string first = runs.CreateRunPath();
        string second = runs.CreateRunPath();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Deleting_a_run_removes_it_and_dispose_does_not_try_again()
    {
        using TemporaryRunSet runs = new(_directory);
        string path = runs.CreateRunPath();
        File.WriteAllBytes(path, [1, 2, 3]);

        runs.Delete(path);

        Assert.False(File.Exists(path));
        runs.Dispose(); // must not throw attempting to delete a path it already forgot
    }

    [Fact]
    public void Disposing_removes_every_run_file_that_still_exists()
    {
        TemporaryRunSet runs = new(_directory);
        string first = runs.CreateRunPath();
        string second = runs.CreateRunPath();
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);

        runs.Dispose();

        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void Creating_run_paths_concurrently_records_every_one_of_them()
    {
        // Every spiller under either strategy creates its run path on its own thread, so
        // this is the ordinary way the type is used, not a stress case. An unsynchronised
        // set can throw from inside its own hash table here, or quietly lose an entry —
        // and a lost entry is a run file Dispose never hears about and never deletes.
        const int threads = 8;
        const int perThread = 500;
        TemporaryRunSet runs = new(_directory);

        string[][] created = new string[threads][];
        Parallel.For(0, threads, thread =>
        {
            string[] paths = new string[perThread];
            for (int i = 0; i < perThread; i++)
            {
                paths[i] = runs.CreateRunPath();
            }

            created[thread] = paths;
        });

        string[] all = [.. created.SelectMany(paths => paths)];
        Assert.Equal(threads * perThread, all.Distinct(StringComparer.Ordinal).Count());

        foreach (string path in all)
        {
            File.WriteAllBytes(path, [1]);
        }

        runs.Dispose();

        // Nothing left behind is the whole point: a dropped entry shows up here as a file
        // that outlived the set that was supposed to own it.
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Disposing_after_an_external_deletion_tolerates_the_missing_file()
    {
        TemporaryRunSet runs = new(_directory);
        string path = runs.CreateRunPath();
        File.WriteAllBytes(path, [1]);
        File.Delete(path); // simulates a deletion TemporaryRunSet never learns about

        Exception? thrown = Record.Exception(runs.Dispose);

        Assert.Null(thrown);
    }

    [Fact]
    public void Disposing_leaves_the_temp_directory_in_place_whether_or_not_this_instance_created_it()
    {
        // The parent is always left alone: two invocations can share one --temp, so
        // neither can safely decide it owns it -- not even the one whose constructor
        // happened to create it, and not merely because it is empty by the time
        // Dispose runs.
        TemporaryRunSet created = new(_directory);
        created.Dispose();
        Assert.True(Directory.Exists(_directory));

        TemporaryRunSet preExisting = new(_directory);
        preExisting.Dispose();
        Assert.True(Directory.Exists(_directory));
    }

    [Fact]
    [Trait("Case", "TR-04")]
    public void Disposing_removes_the_private_directory_even_when_the_parent_already_existed()
    {
        // The private directory is this invocation's own namespace regardless of
        // whether --temp's parent predates the run, so it is always removed on
        // dispose -- unlike the parent itself, which a pre-existing directory keeps.
        Directory.CreateDirectory(_directory);
        TemporaryRunSet runs = new(_directory);
        string path = runs.CreateRunPath();
        File.WriteAllBytes(path, [1]);
        string? privateDirectory = Path.GetDirectoryName(path);

        runs.Dispose();

        Assert.True(Directory.Exists(_directory));
        Assert.NotNull(privateDirectory);
        Assert.False(Directory.Exists(privateDirectory));
    }

    [Fact]
    public void Disposing_leaves_a_temp_directory_with_unrelated_content_in_place()
    {
        // --temp defaults to the output file's own directory, so deleting a non-empty
        // directory here would be destructive: it may hold the output file, or anything
        // else that happens to live alongside it.
        TemporaryRunSet runs = new(_directory);
        string path = runs.CreateRunPath();
        File.WriteAllBytes(path, [1]);
        string unrelated = Path.Combine(_directory, "not-a-run-file.txt");
        File.WriteAllBytes(unrelated, [9, 9, 9]);

        runs.Dispose();

        Assert.True(Directory.Exists(_directory));
        Assert.True(File.Exists(unrelated));
        Assert.False(File.Exists(path)); // the run file itself is still cleaned up
    }
}
