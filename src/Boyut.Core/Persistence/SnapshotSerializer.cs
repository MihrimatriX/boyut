using System.IO.Compression;
using System.Runtime.InteropServices;
using Boyut.Core.Model;

namespace Boyut.Core.Persistence;

/// <summary>
/// Saves and loads a <see cref="FileIndex"/> as a ".boyut" file: an 8-byte magic, a version,
/// then a Brotli stream holding the metadata and the raw arrays. A full C: scan
/// (~1.3 M nodes) compresses to roughly 15–25 MB and loads in well under a second.
/// Snapshots are what history/diff features and the elevated scan helper build on.
/// </summary>
public static class SnapshotSerializer
{
    public const string FileExtension = ".boyut";
    private static ReadOnlySpan<byte> Magic => "BOYUTIDX"u8;
    // v2 adds the totals (so history lists need not decompress the arrays) and the USN checkpoint.
    private const int Version = 2;

    public static void Save(FileIndex index, string path, CompressionLevel level = CompressionLevel.Fastest)
    {
        string temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            Save(index, file, level);
        File.Move(temp, path, overwrite: true);
    }

    public static void Save(FileIndex index, Stream stream, CompressionLevel level = CompressionLevel.Fastest)
    {
        stream.Write(Magic);
        Span<byte> version = stackalloc byte[4];
        BitConverter.TryWriteBytes(version, Version);
        stream.Write(version);

        using var brotli = new BrotliStream(stream, level, leaveOpen: true);
        using var writer = new BinaryWriter(brotli);

        var m = index.Metadata;
        writer.Write(m.RootPath);
        writer.Write(m.Separator);
        writer.Write(m.Engine);
        writer.Write(m.ScannedAtUtc.Ticks);
        writer.Write(m.Duration.Ticks);
        WriteNullable(writer, m.VolumeLabel);
        WriteNullable(writer, m.FileSystem);
        writer.Write(m.VolumeTotalBytes ?? -1);
        writer.Write(m.VolumeFreeBytes ?? -1);
        writer.Write(m.ClusterSize);
        writer.Write(m.ErrorCount);
        writer.Write(index.TotalSize);
        writer.Write(index.TotalAllocated);
        writer.Write(index.TotalFiles);
        writer.Write(m.Usn is not null);
        writer.Write(m.Usn?.JournalId ?? 0);
        writer.Write(m.Usn?.NextUsn ?? 0);

        writer.Write(index.Count);
        writer.Write(index.names.Length);
        writer.Write(index.childOrder.Length);
        writer.Flush();

        WriteArray<int>(brotli, index.parent);
        WriteArray<int>(brotli, index.nameStart);
        WriteArray<ushort>(brotli, index.nameLength);
        WriteArray<char>(brotli, index.names);
        WriteArray<long>(brotli, index.size);
        WriteArray<long>(brotli, index.allocated);
        WriteArray<long>(brotli, index.modified);
        WriteArray<int>(brotli, index.attributes);
        WriteArray<NodeFlags>(brotli, index.flags);
        WriteArray<int>(brotli, index.fileCount);
        WriteArray<int>(brotli, index.childStart);
        WriteArray<int>(brotli, index.childCount);
        WriteArray<int>(brotli, index.childOrder);
    }

    public static FileIndex Load(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Load(file);
    }

    /// <summary>Metadata and totals only: decompresses a few hundred bytes instead of the whole index.</summary>
    public static SnapshotHeader ReadHeader(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
        int version = ReadVersion(file);
        if (version == 1)
        {
            file.Position = 0;
            var index = Load(file);
            return new SnapshotHeader(index.Metadata, index.TotalSize, index.TotalAllocated, index.TotalFiles);
        }
        using var brotli = new BrotliStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(brotli);
        return ReadHeader(reader, version);
    }

    public static FileIndex Load(Stream stream)
    {
        int version = ReadVersion(stream);
        using var brotli = new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(brotli);
        var meta = ReadHeader(reader, version).Metadata;

        int count = reader.ReadInt32();
        int nameChars = reader.ReadInt32();
        int orderLength = reader.ReadInt32();
        if (count <= 0 || nameChars < 0 || orderLength < 0) throw new InvalidDataException("Bozuk tarama dosyası.");

        var parent = ReadArray<int>(brotli, count);
        var nameStart = ReadArray<int>(brotli, count);
        var nameLength = ReadArray<ushort>(brotli, count);
        var names = ReadArray<char>(brotli, nameChars);
        var size = ReadArray<long>(brotli, count);
        var allocated = ReadArray<long>(brotli, count);
        var modified = ReadArray<long>(brotli, count);
        var attributes = ReadArray<int>(brotli, count);
        var flags = ReadArray<NodeFlags>(brotli, count);
        var fileCount = ReadArray<int>(brotli, count);
        var childStart = ReadArray<int>(brotli, count);
        var childCount = ReadArray<int>(brotli, count);
        var childOrder = ReadArray<int>(brotli, orderLength);

        Validate(count, parent, nameStart, nameLength, nameChars, childStart, childCount, childOrder);

        return new FileIndex(meta, count, parent, nameStart, nameLength, names, size, allocated,
            modified, attributes, flags, fileCount, childStart, childCount, childOrder);
    }

    private static int ReadVersion(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual(Magic)) throw new InvalidDataException("Bu bir Boyut tarama dosyası değil.");
        int version = BitConverter.ToInt32(header[8..]);
        if (version is < 1 or > Version) throw new InvalidDataException($"Desteklenmeyen tarama dosyası sürümü: {version}.");
        return version;
    }

    private static SnapshotHeader ReadHeader(BinaryReader reader, int version)
    {
        var meta = new ScanMetadata
        {
            RootPath = reader.ReadString(),
            Separator = reader.ReadChar(),
            Engine = reader.ReadString(),
            ScannedAtUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
            Duration = new TimeSpan(reader.ReadInt64()),
            VolumeLabel = ReadNullable(reader),
            FileSystem = ReadNullable(reader),
        };
        long total = reader.ReadInt64(), free = reader.ReadInt64();
        meta = meta with
        {
            VolumeTotalBytes = total < 0 ? null : total,
            VolumeFreeBytes = free < 0 ? null : free,
            ClusterSize = reader.ReadInt32(),
            ErrorCount = reader.ReadInt32(),
        };
        if (version < 2) return new SnapshotHeader(meta, -1, -1, -1);

        long size = reader.ReadInt64(), allocated = reader.ReadInt64();
        int files = reader.ReadInt32();
        bool hasUsn = reader.ReadBoolean();
        ulong journal = reader.ReadUInt64();
        long next = reader.ReadInt64();
        if (hasUsn) meta = meta with { Usn = new UsnCheckpoint(journal, next) };
        return new SnapshotHeader(meta, size, allocated, files);
    }

    /// <summary>Snapshots may come from anywhere; never trust indices that would read out of bounds.</summary>
    private static void Validate(int count, int[] parent, int[] nameStart, ushort[] nameLength, int nameChars,
        int[] childStart, int[] childCount, int[] childOrder)
    {
        if (parent[0] != -1) throw new InvalidDataException("Bozuk tarama dosyası (kök).");
        for (int i = 0; i < count; i++)
        {
            if (i > 0 && (parent[i] < 0 || parent[i] >= i)) throw new InvalidDataException("Bozuk tarama dosyası (üst düğüm).");
            if (nameStart[i] < 0 || (long)nameStart[i] + nameLength[i] > nameChars) throw new InvalidDataException("Bozuk tarama dosyası (ad).");
            if (childStart[i] < 0 || childCount[i] < 0 || (long)childStart[i] + childCount[i] > childOrder.Length)
                throw new InvalidDataException("Bozuk tarama dosyası (alt düğüm).");
        }
        foreach (int c in childOrder)
            if (c <= 0 || c >= count) throw new InvalidDataException("Bozuk tarama dosyası (sıra).");
    }

    private static void WriteArray<T>(Stream stream, T[] array) where T : unmanaged =>
        stream.Write(MemoryMarshal.AsBytes(array.AsSpan()));

    private static T[] ReadArray<T>(Stream stream, int length) where T : unmanaged
    {
        var array = GC.AllocateUninitializedArray<T>(length);
        stream.ReadExactly(MemoryMarshal.AsBytes(array.AsSpan()));
        return array;
    }

    private static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) writer.Write(value);
    }

    private static string? ReadNullable(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;
}

public sealed record SnapshotHeader(ScanMetadata Metadata, long TotalSize, long TotalAllocated, int TotalFiles);
