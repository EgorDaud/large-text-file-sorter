using FileSorter.Infrastructure;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.Infrastructure;

// TR-05 to TR-07 write to the process-wide Console.Error, which other "Program" tests swap out.
[Collection("Program")]
public sealed class TemporaryRunSetTests : IDisposable
{
    private readonly TempDirectory _scratch = new();
    private readonly string _directory;

    public TemporaryRunSetTests() => _directory = Path.Combine(_scratch.Path, "temp");

    public void Dispose() => _scratch.Dispose();

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
        using TemporaryRunSet runs = new(_directory);

        Assert.Empty(Directory.GetDirectories(_directory));
    }

    [Fact]
    [Trait("Case", "TR-05")]
    public void Deleting_a_run_that_is_held_open_does_not_throw_and_dispose_removes_it_once_released()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Deleting an open file succeeds on Unix; only Windows raises a sharing violation.");

        TemporaryRunSet runs = new(_directory);
        string path = runs.CreateRunPath();
        File.WriteAllBytes(path, [1, 2, 3]);
        string? privateDirectory = Path.GetDirectoryName(path);
        Assert.NotNull(privateDirectory);

        FileStream holder = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Exception? thrown = Record.Exception(() => runs.Delete(path));

            Assert.Null(thrown);
            Assert.True(File.Exists(path));
        }
        finally
        {
            holder.Dispose();
        }

        runs.Dispose();

        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(privateDirectory));
    }

    [Fact]
    [Trait("Case", "TR-06")]
    public void A_run_whose_delete_failed_is_removed_by_the_next_delete_once_released()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Deleting an open file succeeds on Unix; only Windows raises a sharing violation.");

        using TemporaryRunSet runs = new(_directory);
        string held = runs.CreateRunPath();
        string next = runs.CreateRunPath();
        File.WriteAllBytes(held, [1, 2, 3]);
        File.WriteAllBytes(next, [4, 5, 6]);

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            runs.Delete(held);
            Assert.True(File.Exists(held));
        }

        runs.Delete(next);

        Assert.False(File.Exists(held));
        Assert.False(File.Exists(next));
    }

    [Fact]
    [Trait("Case", "TR-07")]
    public void A_run_still_held_at_dispose_is_reported_once_naming_the_private_directory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Deleting an open file succeeds on Unix; only Windows raises a sharing violation.");

        TemporaryRunSet runs = new(_directory);
        string held = runs.CreateRunPath();
        string released = runs.CreateRunPath();
        File.WriteAllBytes(held, [1, 2, 3]);
        File.WriteAllBytes(released, [4, 5, 6]);
        string privateDirectory = Path.GetDirectoryName(held)!;

        string stderr;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stderr = ConsoleCapture.Error(runs.Dispose);
        }

        string[] lines = stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        string line = Assert.Single(lines);
        Assert.Contains(privateDirectory, line, StringComparison.Ordinal);
        Assert.Contains("1 run file(s)", line, StringComparison.Ordinal);
        Assert.True(File.Exists(held));
        Assert.False(File.Exists(released));
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
        runs.Dispose();
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

        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public void Disposing_after_an_external_deletion_tolerates_the_missing_file()
    {
        TemporaryRunSet runs = new(_directory);
        string path = runs.CreateRunPath();
        File.WriteAllBytes(path, [1]);
        File.Delete(path);

        Exception? thrown = Record.Exception(runs.Dispose);

        Assert.Null(thrown);
    }

    [Fact]
    public void Disposing_leaves_the_temp_directory_in_place_whether_or_not_this_instance_created_it()
    {
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
        TemporaryRunSet runs = new(_directory);
        string path = runs.CreateRunPath();
        File.WriteAllBytes(path, [1]);
        string unrelated = Path.Combine(_directory, "not-a-run-file.txt");
        File.WriteAllBytes(unrelated, [9, 9, 9]);

        runs.Dispose();

        Assert.True(Directory.Exists(_directory));
        Assert.True(File.Exists(unrelated));
        Assert.False(File.Exists(path));
    }
}
