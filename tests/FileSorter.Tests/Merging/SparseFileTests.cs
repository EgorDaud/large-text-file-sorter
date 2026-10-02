using FileSorter.Merging;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.Merging;

public sealed class SparseFileTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    [Trait("Case", "SF-01")]
    public void Marking_a_fresh_file_sparse_succeeds_and_the_attribute_appears()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FSCTL_SET_SPARSE is a Windows-only, NTFS-only mechanism.");

        string path = Path.Combine(_directory.Path, "fresh.bin");
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

        const long length = 64 * 1024 * 1024;
        const long writeOffset = 60 * 1024 * 1024;
        byte[] payload = new byte[1024];
        Array.Fill(payload, (byte)0xAB);

        string path = Path.Combine(_directory.Path, "sparse.bin");
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

        // Empirical (NTFS, Windows 11): clearing sparse succeeds once every byte has been written.
        const int length = 4 * 1024 * 1024;
        byte[] block = new byte[64 * 1024];
        Array.Fill(block, (byte)0xCD);

        string path = Path.Combine(_directory.Path, "cleared.bin");
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
