using System.Text;
using FileSorter.RunGeneration;
using FileSorter.Startup;
using Xunit;

namespace FileSorter.Tests.RunGeneration;

// ChunkSpiller writes through a real TemporaryRunSet and has no Stream seam to
// substitute, so these tests touch the file system. Each creates and removes its own
// scratch directory.
public sealed class ChunkSpillerTests : IDisposable
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
    public async Task Spilling_writes_a_sorted_run_with_single_character_terminators()
    {
        using TemporaryRunSet runs = new(_directory);
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
        // The run file is preallocated with SetLength before the first byte is written,
        // sized to the sum of each line's length plus its terminator. expectedLength is
        // computed independently of ChunkSpiller's own arithmetic -- straight from the
        // source strings, not from LineDescriptor.Length. The input is "\r\n"-
        // terminated while expectedLength allows one terminator byte per line, so the
        // two agree only if the CR is stripped.
        string[] lines = ["3. Cherry", "1. Apple", "2. Banana"];
        long expectedLength = lines.Sum(line => Encoding.UTF8.GetByteCount(line) + 1);
        Chunk chunk = await BuildChunkAsync(Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\r\n"))));

        using TemporaryRunSet runs = new(_directory);
        ChunkSpiller spiller = new(runs, spillBufferSize: 4096);

        string path = await spiller.SpillAsync(chunk, TestContext.Current.CancellationToken);

        Assert.Equal(expectedLength, new FileInfo(path).Length);
    }

    [Fact]
    public async Task Spilling_writes_lines_longer_than_the_staging_buffer_directly_mid_chunk_and_at_the_end()
    {
        // The default --max-line and SpillBufferSize are both 64 KiB, so a legal
        // maximum-length line already exceeds the staging buffer at stock settings and
        // has to be written straight through rather than staged. A 64-byte staging
        // buffer stands in for that relationship at a testable size; the chunk's own
        // buffer (4096 bytes below) holds these lines regardless. One over-length line
        // follows two that have partly filled the staging buffer, and a second is the
        // last line in the chunk.
        using TemporaryRunSet runs = new(_directory);
        ChunkSpiller spiller = new(runs, spillBufferSize: 64);

        // ChunkSorter orders by string part first, not by the leading number, so the
        // string parts are chosen A < C < E < G to keep the spilled order the same as
        // the written order and the two long lines in the positions described above.
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
        using TemporaryRunSet runs = new(_directory);
        ChunkSpiller spiller = new(runs, spillBufferSize: 4096);
        (Chunk chunk, BufferPool pool) = await BuildChunkWithPoolAsync("1. Apple\n"u8.ToArray());

        await spiller.SpillAsync(chunk, TestContext.Current.CancellationToken);

        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Spilling_releases_the_pool_slot_when_writing_the_run_throws()
    {
        using TemporaryRunSet runs = new(_directory);
        (Chunk chunk, BufferPool pool) = await BuildChunkWithPoolAsync("1. Apple\n"u8.ToArray());

        // Run paths live inside this instance's own private directory, whose random
        // name is not known ahead of time, so a throwaway CreateRunPath call learns it
        // the same way ChunkSpiller's own call will. A directory sitting at the exact
        // path the next CreateRunPath call is about to hand out then makes FileStream's
        // own open throw, without needing a second real volume.
        string privateDirectory = Path.GetDirectoryName(runs.CreateRunPath())!;
        Directory.CreateDirectory(Path.Combine(privateDirectory, "run-00000002.tmp"));
        ChunkSpiller spiller = new(runs, spillBufferSize: 4096);

        // A FileStream open against a path occupied by a directory fails with
        // UnauthorizedAccessException on Windows and with an IOException ("already
        // exists", from FileMode.CreateNew) on Linux; the slot release is what is under
        // test, not which of the two the platform picks.
        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => spiller.SpillAsync(chunk, TestContext.Current.CancellationToken));
        Assert.True(thrown is IOException or UnauthorizedAccessException, thrown.GetType().FullName);

        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Spilling_an_empty_chunk_writes_an_empty_run_and_still_releases_the_slot()
    {
        using TemporaryRunSet runs = new(_directory);
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
        // Capacity 2, not 1: ChunkReader acquires the second slot and issues its fill
        // before parsing the first, before it can know whether a second chunk will be
        // needed, so even a single fully-exhausting chunk needs room for two slots.
        BufferPool pool = new(bufferSize: 4096, descriptorCapacity: 16, capacity: 2);
        using MemoryStream input = new(data);
        await using ChunkReader reader = new(input, pool, maxLineLength: 1024);
        Chunk? chunk = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        return (chunk ?? throw new InvalidOperationException("Fixture produced no chunk."), pool);
    }
}
