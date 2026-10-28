using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Boyut.Core.Model;
using Microsoft.Win32.SafeHandles;

namespace Boyut.Platform.Windows;

/// <summary>What happened on a volume since a checkpoint, reduced to "which folders must be listed again".</summary>
public sealed record UsnChanges(UsnCheckpoint Next, IReadOnlyCollection<string> ChangedDirectories, int Records);

/// <summary>
/// NTFS change journal. Every create, delete, rename and write leaves a record naming the file and its parent
/// folder, so replaying the journal since the last scan tells exactly which folders need a fresh listing.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UsnJournal
{
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    // Windows 10 1709+. Works without admin rights; every record still names its parent folder.
    private const uint FSCTL_READ_UNPRIVILEGED_USN_JOURNAL = 0x000903AB;
    private const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    private const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
    private const int ERROR_JOURNAL_ENTRY_DELETED = 1181;
    private const uint FILE_NAME_NORMALIZED = 0, VOLUME_NAME_DOS = 0;

    /// <summary>Current journal position, or null when the volume has no active journal / cannot be opened.</summary>
    public static UsnCheckpoint? TryQuery(string anyPathOnVolume)
    {
        try
        {
            using var volume = OpenVolume(anyPathOnVolume);
            return Query(volume);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Folders touched since <paramref name="since"/>. Returns null when the journal was recreated or has already
    /// discarded those records (a full scan is needed then).
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The journal cannot be read without administrator rights.</exception>
    public static UsnChanges? ReadSince(string anyPathOnVolume, UsnCheckpoint since, CancellationToken ct = default)
    {
        using var volume = OpenVolume(anyPathOnVolume);
        var now = Query(volume) ?? throw new IOException("Bu birimde değişiklik günlüğü (USN) etkin değil.");
        if (now.JournalId != since.JournalId) return null;

        var parents = new HashSet<ulong>();
        int records = 0;
        var buffer = new byte[64 * 1024];
        long usn = since.NextUsn;
        var request = new byte[48];

        while (usn < now.NextUsn)
        {
            ct.ThrowIfCancellationRequested();
            // READ_USN_JOURNAL_DATA_V1: StartUsn, ReasonMask, ReturnOnlyOnClose, Timeout, BytesToWaitFor, UsnJournalID,
            // Min/MaxMajorVersion. The unprivileged call rejects the V0 layout with ERROR_INVALID_PARAMETER.
            Array.Clear(request);
            BinaryPrimitives.WriteInt64LittleEndian(request, usn);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), 0xFFFF_FFFF);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(32), since.JournalId);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(40), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(42), 2);

            if (!Control(volume, FSCTL_READ_UNPRIVILEGED_USN_JOURNAL, request, buffer, out int returned))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is ERROR_JOURNAL_ENTRY_DELETED or ERROR_JOURNAL_DELETE_IN_PROGRESS or ERROR_JOURNAL_NOT_ACTIVE) return null;
                if (error == NativeMethods.ERROR_ACCESS_DENIED) throw new UnauthorizedAccessException("Değişiklik günlüğünü okumak için yönetici izni gerekir.");
                throw new IOException($"Değişiklik günlüğü okunamadı (hata {error}).");
            }
            if (returned <= 8) break;

            long next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
            for (int offset = 8; offset + 60 <= returned;)
            {
                var record = buffer.AsSpan(offset);
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record);
                if (length < 60) break;
                ushort major = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
                // USN_RECORD_V2: FileReferenceNumber @8, ParentFileReferenceNumber @16.
                if (major == 2) parents.Add(BinaryPrimitives.ReadUInt64LittleEndian(record[16..]));
                records++;
                offset += length;
            }
            if (next <= usn) break;
            usn = next;
        }

        var directories = new List<string>(parents.Count);
        foreach (ulong frn in parents)
            if (ResolvePath(volume, frn) is { } path) directories.Add(path);
        return new UsnChanges(now, directories, records);
    }

    /// <summary>
    /// A handle to the volume's root folder, not to <c>\\.\C:</c>: the device handle needs admin rights, and opened
    /// without data access it bypasses the file system (FSCTLs fail). The root folder works for every user.
    /// </summary>
    private static SafeFileHandle OpenVolume(string anyPathOnVolume)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(anyPathOnVolume));
        if (root is null || root.Length < 2 || root[1] != ':') throw new ArgumentException("Yalnızca sürücü harfli yerel birimler desteklenir.");
        var h = NativeMethods.CreateFile(root[..2] + @"\", NativeMethods.FILE_READ_ATTRIBUTES,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            0, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (!h.IsInvalid) return h;
        int error = Marshal.GetLastPInvokeError();
        h.Dispose();
        throw new IOException($"{root} açılamadı (hata {error}).");
    }

    private static UsnCheckpoint? Query(SafeFileHandle volume)
    {
        var data = new byte[80];
        if (!Control(volume, FSCTL_QUERY_USN_JOURNAL, default, data, out int returned) || returned < 24)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is ERROR_JOURNAL_NOT_ACTIVE or ERROR_JOURNAL_DELETE_IN_PROGRESS) return null;
            if (error == NativeMethods.ERROR_ACCESS_DENIED) throw new UnauthorizedAccessException("Değişiklik günlüğü sorgulanamadı (erişim engellendi).");
            throw new IOException($"Değişiklik günlüğü sorgulanamadı (hata {error}).");
        }
        // USN_JOURNAL_DATA_V0: UsnJournalID @0, FirstUsn @8, NextUsn @16.
        return new UsnCheckpoint(BinaryPrimitives.ReadUInt64LittleEndian(data), BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16)));
    }

    private static unsafe bool Control(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output, out int returned)
    {
        fixed (byte* i = input)
        fixed (byte* o = output)
            return NativeMethods.DeviceIoControl(handle, code, i, input.Length, o, output.Length, out returned, 0);
    }

    /// <summary>"C:\Users\Ali\Downloads" for a file reference number, or null when it is gone / not accessible.</summary>
    private static unsafe string? ResolvePath(SafeFileHandle volume, ulong frn)
    {
        var id = new NativeMethods.FILE_ID_DESCRIPTOR { dwSize = (uint)sizeof(NativeMethods.FILE_ID_DESCRIPTOR), Type = 0, FileId = (long)frn };
        using var handle = NativeMethods.OpenFileById(volume, ref id, NativeMethods.FILE_READ_ATTRIBUTES,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE, 0, NativeMethods.FILE_FLAG_BACKUP_SEMANTICS);
        if (handle.IsInvalid) return null;
        var buffer = new char[1024];
        while (true)
        {
            uint length;
            fixed (char* b = buffer) length = NativeMethods.GetFinalPathNameByHandle(handle, b, (uint)buffer.Length, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
            if (length == 0) return null;
            if (length < buffer.Length)
            {
                var path = new string(buffer, 0, (int)length);
                return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
            }
            buffer = new char[length + 1];
        }
    }
}
