namespace Boyut.Core.Model;

/// <summary>One directory entry collected by a scanner before it is committed to the builder.</summary>
public struct PendingEntry
{
    /// <summary>Offset of the name inside the caller's name buffer.</summary>
    public int NameOffset;
    public int NameLength;
    public long Size;
    public long Allocated;
    public long ModifiedTicks;
    public int Attributes;
    public NodeFlags Flags;
}

/// <summary>
/// Collects nodes from one or many threads and turns them into a <see cref="FileIndex"/>.
/// Nodes may arrive in any order (the MFT scanner sees children before parents);
/// <see cref="Build"/> renumbers them breadth-first, drops anything not reachable from the root,
/// aggregates directory totals and sorts children by size.
/// </summary>
public sealed class FileIndexBuilder
{
    /// <summary>Use as a parent while the real parent is not known yet; fix later with <see cref="SetParent"/>.</summary>
    public const int UnknownParent = -2;

    private readonly Lock gate = new();

    private int count;
    private int[] parent;
    private int[] nameStart;
    private ushort[] nameLength;
    private long[] size;
    private long[] allocated;
    private long[] modified;
    private int[] attributes;
    private NodeFlags[] flags;
    private char[] names;
    private int namesLength;

    public FileIndexBuilder(int capacity = 4096)
    {
        capacity = Math.Max(capacity, 16);
        parent = new int[capacity];
        nameStart = new int[capacity];
        nameLength = new ushort[capacity];
        size = new long[capacity];
        allocated = new long[capacity];
        modified = new long[capacity];
        attributes = new int[capacity];
        flags = new NodeFlags[capacity];
        names = new char[capacity * 16];
    }

    public int Count => Volatile.Read(ref count);

    public int AddRoot(string rootPath, long modifiedTicks = 0, int attributes = (int)FileAttributes.Directory)
    {
        lock (gate)
        {
            if (count != 0) throw new InvalidOperationException("The root must be the first node.");
            return AddCore(-1, rootPath, 0, 0, modifiedTicks, attributes, NodeFlags.Directory);
        }
    }

    /// <summary>Thread-safe single add.</summary>
    public int Add(int parentNode, ReadOnlySpan<char> name, long size, long allocated,
                   long modifiedTicks, int attributes, NodeFlags flags)
    {
        lock (gate) return AddCore(parentNode, name, size, allocated, modifiedTicks, attributes, flags);
    }

    /// <summary>Single add without locking, for single-threaded producers.</summary>
    public int AddUnsynchronized(int parentNode, ReadOnlySpan<char> name, long size, long allocated,
                                 long modifiedTicks, int attributes, NodeFlags flags) =>
        AddCore(parentNode, name, size, allocated, modifiedTicks, attributes, flags);

    /// <summary>
    /// Thread-safe batch add of one directory's entries. The new nodes get consecutive
    /// indices; the first one is returned.
    /// </summary>
    public int AddBatch(int parentNode, ReadOnlySpan<PendingEntry> entries, ReadOnlySpan<char> namePool)
    {
        lock (gate)
        {
            int first = count;
            EnsureCapacity(count + entries.Length);
            foreach (ref readonly var e in entries)
            {
                AddCore(parentNode, namePool.Slice(e.NameOffset, e.NameLength),
                    e.Size, e.Allocated, e.ModifiedTicks, e.Attributes, e.Flags);
            }
            return first;
        }
    }

    public void SetParent(int node, int parentNode) => parent[node] = parentNode;
    public void AddFlags(int node, NodeFlags extra) { lock (gate) flags[node] |= extra; }
    public NodeFlags GetFlags(int node) => flags[node];

    private int AddCore(int parentNode, ReadOnlySpan<char> name, long sz, long alloc,
                        long modifiedTicks, int attrs, NodeFlags fl)
    {
        if (name.Length > ushort.MaxValue) name = name[..ushort.MaxValue];
        EnsureCapacity(count + 1);
        if (namesLength + name.Length > names.Length)
            Array.Resize(ref names, Math.Max(names.Length * 2, namesLength + name.Length));

        int i = count;
        parent[i] = parentNode;
        nameStart[i] = namesLength;
        nameLength[i] = (ushort)name.Length;
        name.CopyTo(names.AsSpan(namesLength));
        namesLength += name.Length;
        size[i] = sz;
        allocated[i] = alloc;
        modified[i] = modifiedTicks;
        attributes[i] = attrs;
        flags[i] = fl;
        count = i + 1;
        return i;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= parent.Length) return;
        int capacity = Math.Max(needed, parent.Length * 2);
        Array.Resize(ref parent, capacity);
        Array.Resize(ref nameStart, capacity);
        Array.Resize(ref nameLength, capacity);
        Array.Resize(ref size, capacity);
        Array.Resize(ref allocated, capacity);
        Array.Resize(ref modified, capacity);
        Array.Resize(ref attributes, capacity);
        Array.Resize(ref flags, capacity);
    }

    /// <summary>Finalizes the index. The builder must not be used afterwards.</summary>
    public FileIndex Build(ScanMetadata metadata)
    {
        int n = count;
        if (n == 0) throw new InvalidOperationException("No root was added.");

        // 1. Children lists in the original numbering (counting sort on parent).
        var kidCount = new int[n];
        for (int i = 1; i < n; i++)
        {
            int p = parent[i];
            if ((uint)p < (uint)n && p != i) kidCount[p]++;
        }
        var kidStart = new int[n];
        for (int i = 0, run = 0; i < n; i++) { kidStart[i] = run; run += kidCount[i]; }
        var kids = new int[kidStart[n - 1] + kidCount[n - 1]];
        var cursor = (int[])kidStart.Clone();
        for (int i = 1; i < n; i++)
        {
            int p = parent[i];
            if ((uint)p < (uint)n && p != i) kids[cursor[p]++] = i;
        }

        // 2. Breadth-first order from the root. Orphans and cycles are simply never reached.
        var order = new int[n];
        var newIndex = new int[n];
        Array.Fill(newIndex, -1);
        int head = 0, tail = 0;
        order[tail++] = 0;
        newIndex[0] = 0;
        while (head < tail)
        {
            int v = order[head++];
            for (int k = kidStart[v], end = k + kidCount[v]; k < end; k++)
            {
                int c = kids[k];
                if (newIndex[c] >= 0) continue;
                newIndex[c] = tail;
                order[tail++] = c;
            }
        }
        int m = tail;

        // 3. Copy into the final, compact arrays.
        var fParent = new int[m];
        var fNameStart = new int[m];
        var fNameLength = new ushort[m];
        var fSize = new long[m];
        var fAllocated = new long[m];
        var fModified = new long[m];
        var fAttributes = new int[m];
        var fFlags = new NodeFlags[m];
        long totalNameChars = 0;
        for (int k = 0; k < m; k++) totalNameChars += nameLength[order[k]];
        var fNames = new char[totalNameChars];

        int namePos = 0;
        for (int k = 0; k < m; k++)
        {
            int o = order[k];
            fParent[k] = k == 0 ? -1 : newIndex[parent[o]];
            int len = nameLength[o];
            names.AsSpan(nameStart[o], len).CopyTo(fNames.AsSpan(namePos));
            fNameStart[k] = namePos;
            fNameLength[k] = (ushort)len;
            namePos += len;
            fSize[k] = size[o];
            fAllocated[k] = allocated[o];
            fModified[k] = modified[o];
            fAttributes[k] = attributes[o];
            fFlags[k] = flags[o];
        }

        // 4. Children in the new numbering. Parent(k) < k, so filling in ascending order is enough.
        var fChildCount = new int[m];
        for (int k = 1; k < m; k++) fChildCount[fParent[k]]++;
        var fChildStart = new int[m];
        for (int k = 0, run = 0; k < m; k++) { fChildStart[k] = run; run += fChildCount[k]; }
        var fChildOrder = new int[Math.Max(0, m - 1)];
        var fCursor = (int[])fChildStart.Clone();
        for (int k = 1; k < m; k++) fChildOrder[fCursor[fParent[k]]++] = k;

        // 5. Aggregate bottom-up (reverse breadth-first = children before parents).
        var fFileCount = new int[m];
        for (int k = m - 1; k >= 1; k--)
        {
            bool isDir = (fFlags[k] & NodeFlags.Directory) != 0;
            if (!isDir) fFileCount[k] = 1;
            int p = fParent[k];
            bool secondary = (fFlags[k] & NodeFlags.HardLinkSecondary) != 0;
            if (!secondary)
            {
                fSize[p] += fSize[k];
                fAllocated[p] += fAllocated[k];
            }
            fFileCount[p] += fFileCount[k];
        }

        // 6. Children sorted by size (largest first), then by name for stable display.
        var comparer = new SizeDescending(fSize, fNames, fNameStart, fNameLength);
        Parallel.For(0, m, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, k =>
        {
            int c = fChildCount[k];
            if (c > 1) fChildOrder.AsSpan(fChildStart[k], c).Sort(comparer);
        });

        // Release the builder's buffers early; a scan of a full disk can be large.
        parent = []; nameStart = []; nameLength = []; size = []; allocated = [];
        modified = []; attributes = []; flags = []; names = [];
        count = 0;

        return new FileIndex(metadata, m, fParent, fNameStart, fNameLength, fNames,
            fSize, fAllocated, fModified, fAttributes, fFlags,
            fFileCount, fChildStart, fChildCount, fChildOrder);
    }

    private sealed class SizeDescending(long[] size, char[] names, int[] nameStart, ushort[] nameLength) : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            int c = size[b].CompareTo(size[a]);
            if (c != 0) return c;
            return MemoryExtensions.CompareTo(
                names.AsSpan(nameStart[a], nameLength[a]),
                names.AsSpan(nameStart[b], nameLength[b]),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
