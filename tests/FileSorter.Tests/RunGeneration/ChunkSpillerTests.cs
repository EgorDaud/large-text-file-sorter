using System.Text;
using FileSorter.Infrastructure;
using FileSorter.RunGeneration;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.RunGeneration;

public sealed class ChunkSpillerTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Spilling_writes_a_sorted_run_with_single_character_terminators()
    {
        using TemporaryRunSet runs = new(_directory.Path);
        ChunkSpiller spiller = new(runs, spillBufferSize: 4096);
        Chunk chunk = await BuildChunkAsync("3. Cherry\r\n1. Apple\r\n2. Banana\r\n"u8.ToArray());

        string path = await spiller.SpillAsync(chunk, TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain((byte)'\r', written);
        Assert.Equal((byte)'\n', written[^1]);
        Assert.Equal(
            ["1. Apple", "2. Banana", "3. Cherry"],
            Encoding.UTF8.GetString(written).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Spilling_preallocates_the_run_file_to_its_exact_final_length()
    {
        string[] lines = ["3. Cherry", "1. Apple", "2. Banana"];
        long expectedLength = lines.Sum(line => Encoding.UTF8.GetByteCount(line) + 1);
        Chunk chunk = await BuildChunkAsync(Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\r\n"))));

        using TemporaryRunSet runs = new(_directory.Path);
        ChunkSpiller spiller = new(runs, spillBufferSize: 4096);

        string path = await spiller.SpillAsync(chunk, TestContext.Current.CancellationToken);

        Assert.Equal(expectedLength, new FileInfo(path).Length);
    }

    [Fact]
    public async Task Spilling_writes_lines_longer_than_the_staging_buffer_directly_mid_chunk_and_at_the_end()
    {
        using TemporaryRunSet runs = new(_directory.Path);
        ChunkSpiller spiller = new(runs, spillBufferSize: 64);

        // Sorting is by the string part, so A < C < E < G keeps sorted order equal to written order.
        string longMiddle = "2. " + new string('C', 80);
        string longLast = "4. " + new string('G', 90);
        string[] lines = ["1. AAAA", longMiddle, "3. EEEE", longLast];
        Chunk chunk = await BuildChunkAsync(Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n"))));

        string path = await spiller.SpillAsync(chunk, TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        byte[] expected = Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n")));
        Assert.Equal(expected, written);
    }

    [Fact]
    public async Task Spilling_releases_the_pool_slot_on_the_success_path()
    {
        using TemporaryRunSet runs = new(_directory.Path);
        ChunkSpiller spiller = new(runs, spillBufferSize: 4096);
        (Chunk chunk, BufferPool pool) = await BuildChunkWithPoolAsync("1. Apple\n"u8.ToArray());

        await spiller.SpillAsync(chunk, TestContext.Current.CancellationToken);

        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Spilling_releases_the_pool_slot_when_writing_the_run_throws()
    {
        using TemporaryRunSet runs = new(_directory.Path);
        (Chunk chunk, BufferPool pool) = await BuildChunkWithPoolAsync("1. Apple\n"u8.ToArray());

        // The throwaway call takes run 1; a directory at run 2's path makes the spill's open fail.
        string privateDirectory = Path.GetDirectoryName(runs.CreateRunPath())!;
        Directory.CreateDirectory(Path.Combine(privateDirectory, "run-00000002.tmp"));
        ChunkSpiller spiller = new(runs, spillBufferSize: 4096);

        // Windows throws UnauthorizedAccessException here; Linux throws IOException.
        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => spiller.SpillAsync(chunk, TestContext.Current.CancellationToken));
        Assert.True(thrown is IOException or UnauthorizedAccessException, thrown.GetType().FullName);

        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Spilling_after_cancellation_sorts_nothing_creates_no_run_and_still_releases_the_slot()
    {
        using TemporaryRunSet runs = new(_directory.Path);
        ChunkSpiller spiller = new(runs, spillBufferSize: 4096);
        (Chunk chunk, BufferPool pool) = await BuildChunkWithPoolAsync("2. Banana\n1. Apple\n"u8.ToArray());
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => spiller.SpillAsync(chunk, cts.Token));

        Assert.Equal(0, pool.Outstanding);
        Assert.Empty(Directory.GetFileSystemEntries(_directory.Path));   // no private directory, so no run path was ever taken
    }

    [Fact]
    public async Task Spilling_an_empty_chunk_writes_an_empty_run_and_still_releases_the_slot()
    {
        using TemporaryRunSet runs = new(_directory.Path);
        ChunkSpiller spiller = new(runs, spillBufferSize: 4096);
        BufferPool pool = new(bufferSize: 4096, descriptorCapacity: 16, capacity: 1);
        Chunk chunk = new(await pool.AcquireAsync(TestContext.Current.CancellationToken), Count: 0);

        string path = await spiller.SpillAsync(chunk, TestContext.Current.CancellationToken);

        Assert.Empty(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(0, pool.Outstanding);
    }

    private static async Task<Chunk> BuildChunkAsync(byte[] data)
    {
        (Chunk chunk, _) = await BuildChunkWithPoolAsync(data);
        return chunk;
    }

    private static async Task<(Chunk Chunk, BufferPool Pool)> BuildChunkWithPoolAsync(byte[] data)
    {
        // Capacity 2: ChunkReader acquires the next slot before parsing, even for a single chunk.
        BufferPool pool = new(bufferSize: 4096, descriptorCapacity: 16, capacity: 2);
        using MemoryStream input = new(data);
        await using ChunkReader reader = new(input, pool, maxLineLength: 1024);
        Chunk? chunk = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        return (chunk ?? throw new InvalidOperationException("Fixture produced no chunk."), pool);
    }
}
