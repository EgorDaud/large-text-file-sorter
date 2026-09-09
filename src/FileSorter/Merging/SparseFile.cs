using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FileSorter.Merging;

// Windows-only, optional control of the sparse attribute through FSCTL_SET_SPARSE.
// Unsupported filesystems and denied access return false; callers can use the ordinary
// preallocated-file path. Other platforms do not call native code.
internal static class SparseFile
{
    // FSCTL_SET_SPARSE from winioctl.h.
    private const uint FsctlSetSparse = 0x000900C4;

    // Mark before writing past the valid data length so NTFS creates a hole instead of
    // synchronously zero-filling the gap. SetLength itself does not fill the file.
    public static bool TryMarkSparse(SafeFileHandle handle) => TrySetSparse(handle, setSparse: true);

    // Clear only after every byte is written. Clearing a file with holes makes NTFS write
    // them first; a false result leaves a correct file marked sparse.
    public static bool TryClearSparse(SafeFileHandle handle) => TrySetSparse(handle, setSparse: false);

    private static bool TrySetSparse(SafeFileHandle handle, bool setSparse)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return TrySetSparseWindows(handle, setSparse);
    }

    // A null input buffer sets the attribute; FILE_SET_SPARSE_BUFFER clears it.
    [SupportedOSPlatform("windows")]
    private static bool TrySetSparseWindows(SafeFileHandle handle, bool setSparse)
    {
        byte[]? inBuffer = setSparse ? null : [0];
        uint inBufferSize = inBuffer is null ? 0u : (uint)inBuffer.Length;

        // DeviceIoControl reports unsupported filesystems and denied access as false.
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
