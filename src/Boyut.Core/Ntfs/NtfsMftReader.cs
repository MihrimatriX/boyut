using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Boyut.Core.Model;
using Boyut.Core.Scanning;

namespace Boyut.Core.Ntfs;

/// <summary>
/// Builds a <see cref="FileIndex"/> for a whole NTFS volume by reading the Master File Table
/// sequentially, the way WizTree does: one large linear read instead of millions of
/// directory queries. Needs raw read access to the volume (administrator on Windows).
/// </summary>
/// <remarks>
/// Handles: update-sequence fixups, fragmented $MFT (including $DATA extents listed in an
/// $ATTRIBUTE_LIST), extension records, hard links (one node per name, size counted once),
/// stale parent references (sequence numbers), compressed/sparse allocation, CompactOS/WOF
/// compressed files, cloud placeholders and optional alternate data streams.
/// </remarks>
public static class NtfsMftReader
{
    private const int ChunkSize = 4 << 20;

    public static FileIndex ReadVolume(IVolumeReader volume, string rootPath, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var bootBytes = new byte[512];
        volume.ReadExactly(0, bootBytes);
        var boot = NtfsBootSector.Parse(bootBytes);

        var (runs, mftSize) = ReadMftLayout(volume, boot);
        long totalRecords = mftSize / boot.FileRecordSize;
        if (totalRecords <= Ntfs.RootRecord) throw new InvalidDataException("MFT çok küçük.");
        if (totalRecords > int.MaxValue) throw new InvalidDataException("MFT beklenenden büyük.");

        var table = new RecordTable((int)totalRecords, options.IncludeAlternateStreams);
        ReadAllRecords(volume, boot, runs, table, progress, cancellationToken);

        progress?.Report(new ScanProgress(table.RecordsSeen, table.BytesSeen, null, 1, "Ağaç kuruluyor"));
        var builder = BuildTree(table, rootPath, options);

        var meta = new ScanMetadata
        {
            RootPath = rootPath,
            Separator = '\\',
            Engine = "mft",
            ScannedAtUtc = DateTime.UtcNow,
            Duration = stopwatch.Elapsed,
            FileSystem = "NTFS",
            ClusterSize = boot.ClusterSize,
            VolumeTotalBytes = boot.VolumeSize,
        };
        var index = builder.Build(meta);
        return index;
    }

    // ---- $MFT layout ------------------------------------------------------------------------

    private static (List<DataRun> Runs, long Size) ReadMftLayout(IVolumeReader volume, NtfsBootSector boot)
    {
        int rs = boot.FileRecordSize;
        var record0 = new byte[rs];
        volume.ReadExactly(boot.MftLcn * boot.ClusterSize, record0);
        if (!IsFileRecord(record0) || !Ntfs.ApplyFixup(record0))
            throw new InvalidDataException("MFT kaydı 0 okunamadı.");

        var runs = new List<DataRun>();
        long size = -1;
        byte[]? attributeList = null;

        foreach (var attr in EnumerateAttributes(record0))
        {
            if (attr.Type == Ntfs.AttrData && attr.NameLength == 0 && attr.NonResident)
            {
                long startVcn = attr.StartVcn;
                if (startVcn == 0) size = attr.DataSize;
                runs.AddRange(Ntfs.DecodeRunList(attr.RunList, startVcn));
            }
            else if (attr.Type == Ntfs.AttrAttributeList)
            {
                attributeList = attr.NonResident
                    ? ReadNonResident(volume, boot, Ntfs.DecodeRunList(attr.RunList, 0), attr.DataSize)
                    : attr.Value.ToArray();
            }
        }
        if (size <= 0 || runs.Count == 0) throw new InvalidDataException("$MFT veri özniteliği bulunamadı.");

        // A heavily fragmented $MFT keeps the rest of its run list in extension records.
        if (attributeList is not null)
        {
            foreach (var entry in EnumerateAttributeList(attributeList))
            {
                if (entry.Type != Ntfs.AttrData || entry.NameLength != 0 || entry.StartVcn == 0) continue;
                long recordNumber = Ntfs.RecordNumber(entry.BaseReference);
                if (recordNumber == 0) continue;
                var ext = ReadRecordViaRuns(volume, boot, runs, recordNumber);
                if (ext is null) continue;
                foreach (var attr in EnumerateAttributes(ext))
                {
                    if (attr.Type == Ntfs.AttrData && attr.NameLength == 0 && attr.NonResident && attr.StartVcn == entry.StartVcn)
                        runs.AddRange(Ntfs.DecodeRunList(attr.RunList, attr.StartVcn));
                }
            }
        }

        runs.Sort((a, b) => a.Vcn.CompareTo(b.Vcn));
        return (runs, size);
    }

    private static byte[]? ReadRecordViaRuns(IVolumeReader volume, NtfsBootSector boot, List<DataRun> runs, long recordNumber)
    {
        long byteOffset = recordNumber * boot.FileRecordSize;
        long vcn = byteOffset / boot.ClusterSize;
        foreach (var run in runs)
        {
            if (run.Lcn < 0 || vcn < run.Vcn || vcn >= run.Vcn + run.Length) continue;
            long position = (run.Lcn + (vcn - run.Vcn)) * boot.ClusterSize + byteOffset % boot.ClusterSize;
            var record = new byte[boot.FileRecordSize];
            volume.ReadExactly(position, record);
            return IsFileRecord(record) && Ntfs.ApplyFixup(record) ? record : null;
        }
        return null;
    }

    private static byte[] ReadNonResident(IVolumeReader volume, NtfsBootSector boot, List<DataRun> runs, long size)
    {
        var data = new byte[size];
        long written = 0;
        foreach (var run in runs)
        {
            if (written >= size) break;
            long bytes = Math.Min(run.Length * boot.ClusterSize, size - written);
            if (run.Lcn >= 0) volume.ReadExactly(run.Lcn * boot.ClusterSize, data.AsSpan((int)written, (int)bytes));
            written += bytes;
        }
        return data;
    }

    // ---- record pass ------------------------------------------------------------------------

    private static void ReadAllRecords(IVolumeReader volume, NtfsBootSector boot, List<DataRun> runs,
        RecordTable table, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        int rs = boot.FileRecordSize;
        long cs = boot.ClusterSize;
        var buffer = GC.AllocateUninitializedArray<byte>(ChunkSize);
        long total = table.Count;
        long lastReport = 0;

        foreach (var run in runs)
        {
            if (run.Lcn < 0) continue;
            long runBytes = run.Length * cs;
            long firstRecord = run.Vcn * cs / rs;

            for (long chunkOffset = 0; chunkOffset < runBytes; chunkOffset += ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                long recordAtChunk = firstRecord + chunkOffset / rs;
                if (recordAtChunk >= total) break;

                int length = (int)Math.Min(ChunkSize, runBytes - chunkOffset);
                length = (int)Math.Min(length, (total - recordAtChunk) * rs);
                length -= length % rs;
                if (length <= 0) break;

                var span = buffer.AsSpan(0, length);
                volume.ReadExactly(run.Lcn * cs + chunkOffset, span);

                for (int r = 0; r + rs <= length; r += rs)
                    table.Parse(span.Slice(r, rs), recordAtChunk + r / rs);

                table.RecordsSeen += length / rs;
                if (progress is not null && table.RecordsSeen - lastReport >= 65536)
                {
                    lastReport = table.RecordsSeen;
                    progress.Report(new ScanProgress(table.RecordsSeen, table.BytesSeen, null,
                        Math.Min(1.0, (double)table.RecordsSeen / total), "MFT okunuyor"));
                }
            }
        }
    }

    private static FileIndexBuilder BuildTree(RecordTable t, string rootPath, ScanOptions options)
    {
        int total = t.Count;
        var builder = new FileIndexBuilder(Math.Max(1024, t.NameCount + 16));
        int rootRecord = (int)Ntfs.RootRecord;
        int root = builder.AddRoot(rootPath, t.Modified[rootRecord], t.Attributes[rootRecord] | (int)FileAttributes.Directory);

        var nodeOf = new int[total];
        Array.Fill(nodeOf, -1);
        nodeOf[rootRecord] = root;

        var pending = new List<(int Node, long ParentRecord, ushort ParentSequence)>(t.NameCount);
        const int hiddenOrSystem = (int)(FileAttributes.Hidden | FileAttributes.System);

        for (int r = 0; r < total; r++)
        {
            if ((t.State[r] & RecordTable.InUse) == 0 || r == rootRecord) continue;
            int first = t.FirstName[r];
            if (first < 0) continue;

            bool isDir = (t.State[r] & RecordTable.Directory) != 0;
            int attrs = t.Attributes[r];
            if (isDir) attrs |= (int)FileAttributes.Directory;
            if (!options.IncludeHidden && (attrs & hiddenOrSystem) != 0) continue;

            var flags = isDir ? NodeFlags.Directory : NodeFlags.None;
            if ((attrs & (int)FileAttributes.ReparsePoint) != 0) flags |= NodeFlags.ReparsePoint;
            if (!isDir && ExtraFileAttributes.IsCloudPlaceholder(attrs)) flags |= NodeFlags.CloudPlaceholder;

            long size = 0, allocated = 0;
            if (!isDir)
            {
                size = t.Size[r];
                allocated = t.Allocated[r];
                if (t.Extras.TryGetValue(r, out var extra))
                {
                    size += extra.Size;
                    allocated += extra.Allocated;
                }
                t.BytesSeen += size;
            }

            bool primary = true;
            for (int e = first; e >= 0; e = t.Names[e].Next)
            {
                ref readonly var name = ref CollectionsMarshal.AsSpan(t.Names)[e];
                int node = builder.AddUnsynchronized(FileIndexBuilder.UnknownParent,
                    t.NameChars.AsSpan(name.CharStart, name.Length),
                    size, allocated, t.Modified[r], attrs,
                    primary ? flags : flags | NodeFlags.HardLinkSecondary);
                pending.Add((node, name.ParentRecord, name.ParentSequence));
                if (primary) nodeOf[r] = node;
                primary = false;
            }
        }

        foreach (var (node, parentRecord, parentSequence) in pending)
        {
            if (parentRecord < 0 || parentRecord >= total) continue;
            int p = (int)parentRecord;
            bool valid = (t.State[p] & (RecordTable.InUse | RecordTable.Directory)) == (RecordTable.InUse | RecordTable.Directory)
                         && (parentSequence == 0 || t.Sequence[p] == parentSequence);
            if (valid && nodeOf[p] >= 0) builder.SetParent(node, nodeOf[p]);
        }

        return builder;
    }

    // ---- attribute parsing ------------------------------------------------------------------

    internal static bool IsFileRecord(ReadOnlySpan<byte> record) =>
        record.Length >= 48 && record[0] == (byte)'F' && record[1] == (byte)'I' && record[2] == (byte)'L' && record[3] == (byte)'E';

    internal readonly ref struct AttributeView
    {
        public AttributeView(ReadOnlySpan<byte> raw) => Raw = raw;

        public ReadOnlySpan<byte> Raw { get; }
        public uint Type => BinaryPrimitives.ReadUInt32LittleEndian(Raw);
        public bool NonResident => Raw[8] != 0;
        public int NameLength => Raw[9];
        public int NameOffset => BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x0A..]);
        public ushort Flags => BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x0C..]);

        public ReadOnlySpan<char> Name =>
            NameLength == 0 || NameOffset + NameLength * 2 > Raw.Length
                ? default
                : MemoryMarshal.Cast<byte, char>(Raw.Slice(NameOffset, NameLength * 2));

        // resident
        public ReadOnlySpan<byte> Value
        {
            get
            {
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(Raw[0x10..]);
                int offset = BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x14..]);
                return offset + (long)length > Raw.Length ? default : Raw.Slice(offset, length);
            }
        }

        // non-resident
        public long StartVcn => BinaryPrimitives.ReadInt64LittleEndian(Raw[0x10..]);
        public long AllocatedSize => BinaryPrimitives.ReadInt64LittleEndian(Raw[0x28..]);
        public long DataSize => BinaryPrimitives.ReadInt64LittleEndian(Raw[0x30..]);
        public long CompressedSize => Raw.Length >= 0x48 ? BinaryPrimitives.ReadInt64LittleEndian(Raw[0x40..]) : AllocatedSize;

        public bool IsCompressedOrSparse => (Flags & (Ntfs.AttrFlagCompressed | Ntfs.AttrFlagSparse)) != 0;

        /// <summary>Clusters really used (compressed/sparse files report less than their allocation).</summary>
        public long OnDiskSize => IsCompressedOrSparse ? CompressedSize : AllocatedSize;

        public ReadOnlySpan<byte> RunList
        {
            get
            {
                int offset = BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x20..]);
                return offset >= Raw.Length ? default : Raw[offset..];
            }
        }
    }

    internal ref struct AttributeEnumerator
    {
        private readonly ReadOnlySpan<byte> record;
        private readonly int used;
        private int offset;
        private int nextOffset;

        public AttributeEnumerator(ReadOnlySpan<byte> record)
        {
            this.record = record;
            offset = -1;
            nextOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
            used = (int)Math.Min(record.Length, BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]));
        }

        public AttributeView Current { get; private set; }

        public readonly AttributeEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            offset = nextOffset;
            if (offset < 0 || offset + 16 > used) return false;
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == Ntfs.AttrEnd) return false;
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length < 24 || offset + length > used) return false;

            var raw = record.Slice(offset, length);
            bool nonResident = raw[8] != 0;
            if (nonResident && length < 0x40) return false;
            Current = new AttributeView(raw);
            nextOffset = offset + length;
            return true;
        }
    }

    internal static AttributeEnumerator EnumerateAttributes(ReadOnlySpan<byte> record) => new(record);

    private readonly record struct AttributeListEntry(uint Type, int NameLength, long StartVcn, ulong BaseReference);

    private static List<AttributeListEntry> EnumerateAttributeList(ReadOnlySpan<byte> data)
    {
        var list = new List<AttributeListEntry>();
        int pos = 0;
        while (pos + 0x1A <= data.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 4)..]);
            if (length < 0x1A || pos + length > data.Length) break;
            list.Add(new AttributeListEntry(type, data[pos + 6],
                BinaryPrimitives.ReadInt64LittleEndian(data[(pos + 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(data[(pos + 0x10)..])));
            pos += length;
        }
        return list;
    }

    // ---- per-record accumulators ------------------------------------------------------------

    private struct NameEntry
    {
        public long ParentRecord;
        public ushort ParentSequence;
        public int CharStart;
        public byte Length;
        public byte Namespace;
        public int Next;
    }

    private sealed class RecordTable
    {
        public const byte InUse = 1;
        public const byte Directory = 2;

        private readonly bool includeStreams;

        public RecordTable(int count, bool includeStreams)
        {
            Count = count;
            this.includeStreams = includeStreams;
            State = new byte[count];
            Sequence = new ushort[count];
            Attributes = new int[count];
            Modified = new long[count];
            Size = new long[count];
            Allocated = new long[count];
            FirstName = new int[count];
            Array.Fill(FirstName, -1);
            Names = new List<NameEntry>(Math.Min(count, 1 << 22));
            NameChars = new char[Math.Min((long)count * 12, 1 << 26)];
        }

        public int Count { get; }
        public readonly byte[] State;
        public readonly ushort[] Sequence;
        public readonly int[] Attributes;
        public readonly long[] Modified;
        public readonly long[] Size;
        public readonly long[] Allocated;
        public readonly int[] FirstName;
        public readonly List<NameEntry> Names;
        public char[] NameChars;
        private int nameCharsLength;

        /// <summary>Rare per-record additions: WOF-compressed data and alternate streams.</summary>
        public readonly Dictionary<int, (long Size, long Allocated)> Extras = new();

        public long RecordsSeen;
        public long BytesSeen;
        public int NameCount => Names.Count;

        public void Parse(Span<byte> record, long recordNumber)
        {
            if (!IsFileRecord(record) || !Ntfs.ApplyFixup(record)) return;

            ushort headerFlags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
            if ((headerFlags & Ntfs.RecordInUse) == 0) return;

            ulong baseReference = BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]);
            long target = baseReference == 0 ? recordNumber : Ntfs.RecordNumber(baseReference);
            if (target < 0 || target >= Count) return;
            int t = (int)target;

            if (baseReference == 0)
            {
                State[t] |= InUse;
                if ((headerFlags & Ntfs.RecordIsDirectory) != 0) State[t] |= Directory;
                Sequence[t] = BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..]);
            }

            foreach (var attr in EnumerateAttributes(record))
            {
                switch (attr.Type)
                {
                    case Ntfs.AttrStandardInformation when !attr.NonResident:
                    {
                        var v = attr.Value;
                        if (v.Length < 0x24) break;
                        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(v[0x08..]);
                        Modified[t] = fileTime > 0 ? fileTime + Ntfs.FileTimeEpochTicks : 0;
                        Attributes[t] = (int)BinaryPrimitives.ReadUInt32LittleEndian(v[0x20..]);
                        break;
                    }

                    case Ntfs.AttrFileName when !attr.NonResident:
                    {
                        var v = attr.Value;
                        if (v.Length < 0x42) break;
                        int length = v[0x40];
                        byte ns = v[0x41];
                        if (ns == Ntfs.NamespaceDos || 0x42 + length * 2 > v.Length) break;
                        ulong parent = BinaryPrimitives.ReadUInt64LittleEndian(v);
                        AddName(t, parent, MemoryMarshal.Cast<byte, char>(v.Slice(0x42, length * 2)), ns);
                        break;
                    }

                    case Ntfs.AttrData:
                        ParseData(t, attr);
                        break;
                }
            }
        }

        private void ParseData(int t, AttributeView attr)
        {
            if (attr.NameLength == 0)
            {
                if (!attr.NonResident)
                {
                    Size[t] = attr.Value.Length;
                    Allocated[t] = 0; // lives inside the MFT record itself
                }
                else if (attr.StartVcn == 0)
                {
                    Size[t] = attr.DataSize;
                    Allocated[t] = attr.OnDiskSize;
                }
                return;
            }

            // CompactOS / WOF: the real (compressed) bytes sit in this named stream.
            if (attr.Name.SequenceEqual("WofCompressedData"))
            {
                if (attr.NonResident && attr.StartVcn == 0) AddExtra(t, 0, attr.OnDiskSize);
                return;
            }

            if (!includeStreams) return;
            if (!attr.NonResident) AddExtra(t, attr.Value.Length, 0);
            else if (attr.StartVcn == 0) AddExtra(t, attr.DataSize, attr.OnDiskSize);
        }

        private void AddExtra(int t, long size, long allocated)
        {
            Extras.TryGetValue(t, out var current);
            Extras[t] = (current.Size + size, current.Allocated + allocated);
        }

        private void AddName(int t, ulong parentReference, ReadOnlySpan<char> name, byte ns)
        {
            long parentRecord = Ntfs.RecordNumber(parentReference);
            ushort parentSequence = Ntfs.SequenceNumber(parentReference);

            // The same name can appear in both the base and an extension record in odd cases; keep one.
            for (int e = FirstName[t]; e >= 0; e = Names[e].Next)
            {
                var existing = Names[e];
                if (existing.ParentRecord == parentRecord &&
                    NameChars.AsSpan(existing.CharStart, existing.Length).SequenceEqual(name))
                    return;
            }

            if (nameCharsLength + name.Length > NameChars.Length)
                Array.Resize(ref NameChars, Math.Max(NameChars.Length * 2, nameCharsLength + name.Length));
            name.CopyTo(NameChars.AsSpan(nameCharsLength));

            var entry = new NameEntry
            {
                ParentRecord = parentRecord,
                ParentSequence = parentSequence,
                CharStart = nameCharsLength,
                Length = (byte)name.Length,
                Namespace = ns,
                Next = -1,
            };
            nameCharsLength += name.Length;

            int index = Names.Count;
            Names.Add(entry);

            // Append at the tail so the first name seen stays the primary one.
            if (FirstName[t] < 0) { FirstName[t] = index; return; }
            int last = FirstName[t];
            while (Names[last].Next >= 0) last = Names[last].Next;
            var tail = Names[last];
            tail.Next = index;
            Names[last] = tail;
        }
    }
}
