using System.Buffers.Binary;

namespace Boyut.Core.Ntfs;

/// <summary>The fields of the NTFS boot sector we need.</summary>
public sealed record NtfsBootSector(
    int BytesPerSector,
    int SectorsPerCluster,
    long TotalSectors,
    long MftLcn,
    int FileRecordSize,
    ulong SerialNumber)
{
    public int ClusterSize => BytesPerSector * SectorsPerCluster;
    public long VolumeSize => TotalSectors * BytesPerSector;

    public static NtfsBootSector Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512 || !sector.Slice(3, 8).SequenceEqual("NTFS    "u8))
            throw new InvalidDataException("Bu birim NTFS değil.");

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[0x0B..]);
        byte rawSpc = sector[0x0D];
        int sectorsPerCluster = rawSpc <= 0x80 ? rawSpc : 1 << (256 - rawSpc);
        long totalSectors = BinaryPrimitives.ReadInt64LittleEndian(sector[0x28..]);
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(sector[0x30..]);
        sbyte clustersPerRecord = (sbyte)sector[0x40];
        int clusterSize = bytesPerSector * sectorsPerCluster;
        int recordSize = clustersPerRecord > 0 ? clustersPerRecord * clusterSize : 1 << -clustersPerRecord;
        ulong serial = BinaryPrimitives.ReadUInt64LittleEndian(sector[0x48..]);

        if (bytesPerSector is < 256 or > 4096 || sectorsPerCluster <= 0 || recordSize is < 256 or > 65536 || mftLcn <= 0)
            throw new InvalidDataException("NTFS önyükleme kesimi geçersiz.");

        return new NtfsBootSector(bytesPerSector, sectorsPerCluster, totalSectors, mftLcn, recordSize, serial);
    }
}

/// <summary>A run of clusters. <see cref="Lcn"/> is -1 for sparse runs.</summary>
public readonly record struct DataRun(long Vcn, long Lcn, long Length);

internal static class Ntfs
{
    public const uint AttrStandardInformation = 0x10;
    public const uint AttrAttributeList = 0x20;
    public const uint AttrFileName = 0x30;
    public const uint AttrData = 0x80;
    public const uint AttrEnd = 0xFFFF_FFFF;

    public const ushort RecordInUse = 0x0001;
    public const ushort RecordIsDirectory = 0x0002;

    public const ushort AttrFlagCompressed = 0x0001;
    public const ushort AttrFlagSparse = 0x8000;

    public const byte NamespacePosix = 0;
    public const byte NamespaceWin32 = 1;
    public const byte NamespaceDos = 2;
    public const byte NamespaceWin32AndDos = 3;

    public const long RootRecord = 5;

    /// <summary>FILETIME (100 ns since 1601) → DateTime ticks (100 ns since 0001).</summary>
    public const long FileTimeEpochTicks = 504_911_232_000_000_000;

    public static long RecordNumber(ulong reference) => (long)(reference & 0x0000_FFFF_FFFF_FFFF);
    public static ushort SequenceNumber(ulong reference) => (ushort)(reference >> 48);

    /// <summary>
    /// Undoes the "update sequence" protection: the last two bytes of every 512-byte block were
    /// replaced on disk by a check value; the real bytes live in the update sequence array.
    /// Returns false for torn (partially written) records.
    /// </summary>
    public static bool ApplyFixup(Span<byte> record)
    {
        if (record.Length < 48) return false;
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x04..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[0x06..]);
        if (usaCount == 0 || usaOffset + usaCount * 2 > record.Length) return false;

        ushort check = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * 512 - 2;
            if (pos + 2 > record.Length) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[pos..]) != check) return false;
            record[pos] = record[usaOffset + i * 2];
            record[pos + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    public static List<DataRun> DecodeRunList(ReadOnlySpan<byte> runs, long startingVcn)
    {
        var result = new List<DataRun>();
        long vcn = startingVcn, lcn = 0;
        int pos = 0;
        while (pos < runs.Length)
        {
            byte header = runs[pos++];
            if (header == 0) break;
            int lengthSize = header & 0x0F, offsetSize = header >> 4;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8 || pos + lengthSize + offsetSize > runs.Length) break;

            long length = ReadUnsigned(runs.Slice(pos, lengthSize));
            pos += lengthSize;

            if (offsetSize == 0)
            {
                result.Add(new DataRun(vcn, -1, length));
            }
            else
            {
                lcn += ReadSigned(runs.Slice(pos, offsetSize));
                pos += offsetSize;
                result.Add(new DataRun(vcn, lcn, length));
            }
            vcn += length;
        }
        return result;
    }

    private static long ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int i = bytes.Length - 1; i >= 0; i--) value = (value << 8) | bytes[i];
        return value;
    }

    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        long value = ReadUnsigned(bytes);
        int bits = bytes.Length * 8;
        if (bits < 64 && (value & (1L << (bits - 1))) != 0) value -= 1L << bits;
        return value;
    }
}
