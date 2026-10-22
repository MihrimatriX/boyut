using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Boyut.Platform.Windows;

internal static partial class NativeMethods
{
    public const uint GENERIC_READ = 0x8000_0000;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x0200_0000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x0020_0000;
    public const uint INVALID_FILE_SIZE = 0xFFFF_FFFF;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_CANCELLED = 1223;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint numberOfFreeClusters, out uint totalNumberOfClusters);

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    // --- change journal ---
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte* inBuffer, int inBufferSize,
        byte* outBuffer, int outBufferSize, out int bytesReturned, nint overlapped);

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ID_DESCRIPTOR
    {
        public uint dwSize;
        public int Type;      // 0 = FileIdType (64-bit file reference number)
        public long FileId;   // union with a 128-bit id; padded below to its size
        private long padding;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FILE_ID_DESCRIPTOR fileId, uint desiredAccess,
        uint shareMode, nint securityAttributes, uint flagsAndAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint pathLength, uint flags);

    // --- elevation ---
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevation = 20;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(nint token, int infoClass, out int info, int length, out int returnLength);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    // --- shell: recycle bin ---
    public const uint FO_DELETE = 3;
    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOERRORUI = 0x0400;
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>x64/ARM64 layout (natural alignment). Boyut ships only for 64-bit Windows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        public nint pFrom;
        public nint pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public nint lpszProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    public static partial int SHFileOperation(ref SHFILEOPSTRUCTW op);

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHEmptyRecycleBin(nint hwnd, string? rootPath, uint flags);
}
