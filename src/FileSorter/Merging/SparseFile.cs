using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FileSorter.Merging;

// Windows-only; false (other OS, unsupported filesystem, denied access) means use the plain preallocated file.
internal static class SparseFile
{
    // FSCTL_SET_SPARSE from winioctl.h.
    private const uint FsctlSetSparse = 0x000900C4;

    // Mark before writing past the valid data length, or NTFS zero-fills the gap synchronously.
    public static bool TryMarkSparse(SafeFileHandle handle) => TrySetSparse(handle, setSparse: true);

    // Clear only after every byte is written: clearing a file with holes makes NTFS fill them.
    public static bool TryClearSparse(SafeFileHandle handle) => TrySetSparse(handle, setSparse: false);

    private static bool TrySetSparse(SafeFileHandle handle, bool setSparse)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return TrySetSparseWindows(handle, setSparse);
    }

    // A null input buffer sets the attribute; a FILE_SET_SPARSE_BUFFER of { 0 } clears it.
    [SupportedOSPlatform("windows")]
    private static bool TrySetSparseWindows(SafeFileHandle handle, bool setSparse)
    {
        byte[]? inBuffer = setSparse ? null : [0];
        uint inBufferSize = inBuffer is null ? 0u : (uint)inBuffer.Length;

        return DeviceIoControl(
            handle, FsctlSetSparse, inBuffer, inBufferSize, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
