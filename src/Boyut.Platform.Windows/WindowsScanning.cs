using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Boyut.Core.Model;
using Boyut.Core.Ntfs;
using Boyut.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace Boyut.Platform.Windows;

/// <summary>Real on-disk size from NTFS for compressed, sparse and cloud files; cluster rounding otherwise.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAllocation : IAllocationSizeProvider
{
    private const int Special = (int)(FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.Offline)
                                | ExtraFileAttributes.RecallOnDataAccess | ExtraFileAttributes.RecallOnOpen;

    public WindowsAllocation(string anyPathOnVolume)
    {
        ClusterSize = 4096;
        string? root = Path.GetPathRoot(Path.GetFullPath(anyPathOnVolume));
        if (root is not null && NativeMethods.GetDiskFreeSpace(root, out uint spc, out uint bps, out _, out _))
            ClusterSize = (int)(spc * bps);
    }

    public int ClusterSize { get; }

    public long GetAllocatedSize(ReadOnlySpan<char> fullPath, long logicalSize, int attributes)
    {
        if ((attributes & Special) != 0)
        {
            uint low = NativeMethods.GetCompressedFileSize(LongPath.Of(fullPath), out uint high);
            if (low != NativeMethods.INVALID_FILE_SIZE || Marshal.GetLastPInvokeError() == 0)
            {
                long onDisk = ((long)high << 32) | low;
                return Round(onDisk);
            }
        }
        return Round(logicalSize);
    }

    private long Round(long bytes) => bytes <= 0 ? 0 : (bytes + ClusterSize - 1) / ClusterSize * ClusterSize;
}

/// <summary>Raw, read-only handle to a volume such as <c>\\.\C:</c>.</summary>
[SupportedOSPlatform("windows")]
public sealed class VolumeHandleReader : IVolumeReader
{
    private readonly SafeFileHandle handle;

    private VolumeHandleReader(SafeFileHandle handle) => this.handle = handle;

    /// <summary>4096 covers both 512-byte and 4K-native sectors.</summary>
    public int Alignment => 4096;

    /// <param name="driveLetter">"C" or "C:".</param>
    public static VolumeHandleReader Open(string driveLetter)
    {
        string path = @"\\.\" + driveLetter.TrimEnd('\\', ':') + ":";
        var h = NativeMethods.CreateFile(path, NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            0, NativeMethods.OPEN_EXISTING, 0, 0);
        if (h.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            h.Dispose();
            if (error == NativeMethods.ERROR_ACCESS_DENIED)
                throw new UnauthorizedAccessException("Birimi doğrudan okumak için yönetici izni gerekir.");
            throw new IOException($"{path} açılamadı (hata {error}).");
        }
        return new VolumeHandleReader(h);
    }

    public int Read(long offset, Span<byte> buffer) => RandomAccess.Read(handle, buffer, offset);

    public void Dispose() => handle.Dispose();
}

/// <summary>Reads the NTFS Master File Table directly. Requires administrator rights.</summary>
[SupportedOSPlatform("windows")]
public sealed class MftScanner : IScanner
{
    public string Engine => "mft";

    public Task<FileIndex> ScanAsync(string rootPath, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() => Scan(rootPath, options, progress, cancellationToken),
            cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static FileIndex Scan(string rootPath, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string root = ManagedScanner.NormalizeRoot(rootPath);
        string volumeRoot = Path.GetPathRoot(root) ?? throw new ArgumentException("Geçersiz yol.", nameof(rootPath));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        // Before reading: anything that changes during the scan is replayed by the next refresh.
        var usn = UsnJournal.TryQuery(volumeRoot);
        FileIndex index;
        using (var reader = VolumeHandleReader.Open(volumeRoot[..1]))
            index = NtfsMftReader.ReadVolume(reader, volumeRoot, options, progress, ct);

        if (!string.Equals(root.TrimEnd('\\'), volumeRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            int node = index.FindByPath(root);
            if (node < 0) throw new DirectoryNotFoundException(root);
            index = index.Subtree(node);
        }

        var drive = new DriveInfo(volumeRoot);
        string? label = null;
        try { label = drive.VolumeLabel; } catch (IOException) { }
        var meta = index.Metadata with
        {
            Duration = stopwatch.Elapsed,
            VolumeLabel = label,
            VolumeTotalBytes = drive.TotalSize,
            VolumeFreeBytes = drive.AvailableFreeSpace,
            Usn = usn,
        };
        return index.WithMetadata(meta);
    }
}

/// <summary>Records the change-journal position before a portable scan so it can be refreshed later.</summary>
[SupportedOSPlatform("windows")]
internal sealed class UsnCheckpointScanner(IScanner inner) : IScanner
{
    public string Engine => inner.Engine;

    public async Task<FileIndex> ScanAsync(string rootPath, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var usn = ScannerFactory.IsLocalNtfs(rootPath) ? UsnJournal.TryQuery(rootPath) : null;
        var index = await inner.ScanAsync(rootPath, options, progress, cancellationToken).ConfigureAwait(false);
        return usn is null ? index : index.WithMetadata(index.Metadata with { Usn = usn });
    }
}

/// <summary>"Güncelle": replays the NTFS change journal since the scan instead of scanning everything again.</summary>
[SupportedOSPlatform("windows")]
public static class UsnRefresher
{
    /// <returns>The updated index, or null when the journal cannot vouch for the gap (no checkpoint, journal
    /// recreated or wrapped) and a full scan is needed.</returns>
    /// <exception cref="UnauthorizedAccessException">Reading the journal needs administrator rights here.</exception>
    public static FileIndex? TryRefresh(FileIndex old, ScanOptions options, CancellationToken ct = default)
    {
        string root = old.Metadata.RootPath;
        if (old.Metadata.Usn is not { } since || !Directory.Exists(root)) return null;
        var stopwatch = Stopwatch.StartNew();
        var changes = UsnJournal.ReadSince(root, since, ct);
        if (changes is null) return null;

        var lister = new ManagedDirectoryLister(options, new WindowsAllocation(root));
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        var meta = old.Metadata with
        {
            ScannedAtUtc = DateTime.UtcNow,
            Usn = changes.Next,
            VolumeFreeBytes = drive.AvailableFreeSpace,
            VolumeTotalBytes = drive.TotalSize,
        };
        var index = IncrementalUpdate.Apply(old, changes.ChangedDirectories, lister, meta);
        return index.WithMetadata(index.Metadata with { Duration = stopwatch.Elapsed });
    }
}

public enum ScanEngine { Auto, Mft, Managed }

public static class ScannerFactory
{
    /// <summary>True when the MFT engine can run in this process (Windows, elevated, local NTFS volume).</summary>
    public static bool CanUseMft(string root) =>
        OperatingSystem.IsWindows() && Elevation.IsElevated && IsLocalNtfs(root);

    public static bool IsLocalNtfs(string root)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            string? volume = Path.GetPathRoot(Path.GetFullPath(root));
            if (volume is null || volume.Length != 3 || volume[1] != ':') return false; // not UNC
            var drive = new DriveInfo(volume);
            return drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable &&
                   string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static IScanner Create(ScanEngine engine, string root)
    {
        if (OperatingSystem.IsWindows())
        {
            if (engine == ScanEngine.Mft || (engine == ScanEngine.Auto && CanUseMft(root))) return new MftScanner();
            return new UsnCheckpointScanner(new ManagedScanner(new WindowsAllocation(root)));
        }
        if (engine == ScanEngine.Mft) throw new PlatformNotSupportedException("MFT taraması yalnızca Windows'ta çalışır.");
        return new ManagedScanner();
    }
}
