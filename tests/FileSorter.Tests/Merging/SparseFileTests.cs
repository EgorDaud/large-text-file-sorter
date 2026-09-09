using FileSorter.Merging;
using Xunit;

namespace FileSorter.Tests.Merging;

/// <summary>
/// <see cref="SparseFile"/> is a thin, Windows-only wrapper around FSCTL_SET_SPARSE, so
/// these tests exercise the real filesystem rather than a stub: there is no in-memory
/// stand-in for "does NTFS actually track a hole instead of zero-filling it."
/// </summary>
public sealed class SparseFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "FileSorterTests", Guid.NewGuid().ToString("N"));

    public SparseFileTests()
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
    [Trait("Case", "SF-01")]
    public void Marking_a_fresh_file_sparse_succeeds_and_the_attribute_appears()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FSCTL_SET_SPARSE is a Windows-only, NTFS-only mechanism.");

        string path = Path.Combine(_directory, "fresh.bin");
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
        {
            Assert.SkipUnless(SparseFile.TryMarkSparse(stream.SafeFileHandle), "this volume does not support sparse files.");
        }

        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.SparseFile));
    }

    [Fact]
    [Trait("Case", "SF-02")]
    public void A_write_far_past_valid_data_length_on_a_sparse_file_lands_correctly_and_the_hole_reads_as_zero()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FSCTL_SET_SPARSE is a Windows-only, NTFS-only mechanism.");

        const long length = 64 * 1024 * 1024; // 64 MiB
        const long writeOffset = 60 * 1024 * 1024; // 60 MiB in, comfortably past VDL 0
        byte[] payload = new byte[1024];
        Array.Fill(payload, (byte)0xAB);

        string path = Path.Combine(_directory, "sparse.bin");
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.SkipUnless(SparseFile.TryMarkSparse(stream.SafeFileHandle), "this volume does not support sparse files.");
            stream.SetLength(length);

            stream.Position = writeOffset;
            stream.Write(payload);
            stream.Flush();
        }

        using (FileStream verify = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // The untouched region before the write -- the hole the sparse marking exists
            // to avoid zero-filling on disk -- still reads back as zero, exactly as an
            // ordinary zero-filled extension would, because a hole is a promise about what
            // NTFS stores, not about what a reader observes.
            byte[] holeProbe = new byte[4096];
            verify.Position = 0;
            int readAtStart = verify.ReadAtLeast(holeProbe, holeProbe.Length, throwOnEndOfStream: false);
            Assert.Equal(holeProbe.Length, readAtStart);
            Assert.All(holeProbe, b => Assert.Equal(0, b));

            byte[] readBack = new byte[payload.Length];
            verify.Position = writeOffset;
            int readAtWrite = verify.ReadAtLeast(readBack, readBack.Length, throwOnEndOfStream: false);
            Assert.Equal(readBack.Length, readAtWrite);
            Assert.Equal(payload, readBack);

            Assert.Equal(length, verify.Length);
        }
    }

    [Fact]
    [Trait("Case", "SF-03")]
    public void Clearing_a_fully_written_sparse_file_succeeds_on_this_machine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FSCTL_SET_SPARSE is a Windows-only, NTFS-only mechanism.");

        // Verified empirically on this project's development machine (NTFS, Windows 11):
        // FSCTL_SET_SPARSE with SetSparse = FALSE succeeds once every byte of the file has
        // actually been written, because there is then no sparse range left for NTFS to
        // track. This test pins that finding rather than guessing at it; if a future NTFS
        // or a different volume ever refuses the clear on a fully written file, this is
        // the test that will say so.
        const int length = 4 * 1024 * 1024; // 4 MiB, small enough to fill entirely and quickly
        byte[] block = new byte[64 * 1024];
        Array.Fill(block, (byte)0xCD);

        string path = Path.Combine(_directory, "cleared.bin");
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.SkipUnless(SparseFile.TryMarkSparse(stream.SafeFileHandle), "this volume does not support sparse files.");
            stream.SetLength(length);

            for (int offset = 0; offset < length; offset += block.Length)
            {
                stream.Position = offset;
                stream.Write(block);
            }

            stream.Flush();
            Assert.True(SparseFile.TryClearSparse(stream.SafeFileHandle));
        }

        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.SparseFile));
    }
}
