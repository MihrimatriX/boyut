using System.Runtime.CompilerServices;

namespace Boyut.Core.Model;

/// <summary>
/// Immutable result of a scan, stored as parallel arrays ("struct of arrays").
/// One million nodes take roughly 85 MB (≈ 60 bytes of arrays + the name) instead of the several hundred MB an
/// object-per-node tree would need, and every query is a tight loop over arrays.
/// </summary>
/// <remarks>
/// Invariants (guaranteed by <see cref="FileIndexBuilder.Build"/>):
/// node 0 is the root, nodes are numbered in breadth-first order so <c>Parent(i) &lt; i</c>,
/// directory sizes are aggregated, and each node's children are sorted by size, largest first.
/// </remarks>
public sealed class FileIndex
{
    public const int RootNode = 0;

    internal readonly int[] parent;
    internal readonly int[] nameStart;
    internal readonly ushort[] nameLength;
    internal readonly char[] names;
    internal readonly long[] size;
    internal readonly long[] allocated;
    internal readonly long[] modified;
    internal readonly int[] attributes;
    internal readonly NodeFlags[] flags;
    internal readonly int[] fileCount;
    internal readonly int[] childStart;
    internal readonly int[] childCount;
    internal readonly int[] childOrder;

    internal FileIndex(
        ScanMetadata metadata, int count,
        int[] parent, int[] nameStart, ushort[] nameLength, char[] names,
        long[] size, long[] allocated, long[] modified, int[] attributes, NodeFlags[] flags,
        int[] fileCount, int[] childStart, int[] childCount, int[] childOrder)
    {
        Metadata = metadata;
        Count = count;
        this.parent = parent;
        this.nameStart = nameStart;
        this.nameLength = nameLength;
        this.names = names;
        this.size = size;
        this.allocated = allocated;
        this.modified = modified;
        this.attributes = attributes;
        this.flags = flags;
        this.fileCount = fileCount;
        this.childStart = childStart;
        this.childCount = childCount;
        this.childOrder = childOrder;
    }

    public ScanMetadata Metadata { get; }

    /// <summary>Same nodes, different metadata (e.g. volume info added after the scan). The root path must not change.</summary>
    public FileIndex WithMetadata(ScanMetadata metadata) =>
        new(metadata, Count, parent, nameStart, nameLength, names, size, allocated, modified, attributes, flags,
            fileCount, childStart, childCount, childOrder);

    /// <summary>Number of nodes, root included.</summary>
    public int Count { get; }

    public char Separator => Metadata.Separator;
    public long TotalSize => size[RootNode];
    public long TotalAllocated => allocated[RootNode];
    public int TotalFiles => fileCount[RootNode];

    // ---- per-node accessors -------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Parent(int node) => parent[node];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> NameSpan(int node) => names.AsSpan(nameStart[node], nameLength[node]);

    public string GetName(int node) => new(NameSpan(node));

    /// <summary>Logical size. For directories: sum of everything below (hard links counted once).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Size(int node) => size[node];

    /// <summary>Bytes actually used on disk (cluster rounding, compression, sparse files, cloud placeholders).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Allocated(int node) => allocated[node];

    /// <summary>Size this node contributes to its parent (0 for secondary hard-link names).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long EffectiveSize(int node) => (flags[node] & NodeFlags.HardLinkSecondary) != 0 ? 0 : size[node];

    public long ModifiedTicks(int node) => modified[node];

    public DateTime? ModifiedUtc(int node)
    {
        long t = modified[node];
        return t <= 0 ? null : new DateTime(t, DateTimeKind.Utc);
    }

    public FileAttributes Attributes(int node) => (FileAttributes)attributes[node];
    public int RawAttributes(int node) => attributes[node];
    public NodeFlags Flags(int node) => flags[node];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDirectory(int node) => (flags[node] & NodeFlags.Directory) != 0;

    /// <summary>Files: 1. Directories: number of files anywhere below.</summary>
    public int FileCount(int node) => fileCount[node];

    public int ChildCount(int node) => childCount[node];

    /// <summary>Children sorted by size, largest first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> Children(int node) => childOrder.AsSpan(childStart[node], childCount[node]);

    /// <summary>Extension without the dot, empty for directories and extension-less files.</summary>
    public ReadOnlySpan<char> ExtensionSpan(int node)
    {
        if (IsDirectory(node)) return default;
        var name = NameSpan(node);
        int dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? default : name[(dot + 1)..];
    }

    public int Depth(int node)
    {
        int d = 0;
        while (node > RootNode) { node = parent[node]; d++; }
        return d;
    }

    public bool IsAncestorOrSelf(int ancestor, int node)
    {
        // Parent(i) < i always holds, so we can stop as soon as we go below the candidate.
        while (node > ancestor) node = parent[node];
        return node == ancestor;
    }

    // ---- paths ----------------------------------------------------------------------------

    public string GetFullPath(int node)
    {
        Span<char> stack = stackalloc char[512];
        if (TryWriteFullPath(node, stack, out int written)) return new string(stack[..written]);
        var heap = new char[written];
        TryWriteFullPath(node, heap, out written);
        return new string(heap, 0, written);
    }

    /// <summary>
    /// Writes the full path without allocating. Returns false (and the required length in
    /// <paramref name="written"/>) when the destination is too small.
    /// </summary>
    public bool TryWriteFullPath(int node, Span<char> destination, out int written)
    {
        string root = Metadata.RootPath;
        if (node == RootNode)
        {
            written = root.Length;
            if (root.Length > destination.Length) return false;
            root.AsSpan().CopyTo(destination);
            return true;
        }

        bool rootEndsWithSeparator = root.Length > 0 && IsSeparator(root[^1]);
        int length = rootEndsWithSeparator ? root.Length - 1 : root.Length;
        for (int n = node; n != RootNode; n = parent[n]) length += nameLength[n] + 1;

        written = length;
        if (length > destination.Length) return false;

        int pos = length;
        for (int n = node; n != RootNode; n = parent[n])
        {
            var name = NameSpan(n);
            pos -= name.Length;
            name.CopyTo(destination[pos..]);
            destination[--pos] = Separator;
        }

        // pos now points at the separator that follows the root; restore it if the root already has one.
        root.AsSpan(0, rootEndsWithSeparator ? root.Length - 1 : root.Length).CopyTo(destination);
        return true;
    }

    /// <summary>Path relative to the scan root, e.g. <c>Users\Ali\Downloads</c>.</summary>
    public string GetRelativePath(int node)
    {
        if (node == RootNode) return string.Empty;
        string full = GetFullPath(node);
        string root = Metadata.RootPath;
        int skip = root.Length;
        if (skip < full.Length && !IsSeparator(root[^1])) skip++;
        return full[skip..];
    }

    /// <summary>Finds a node by absolute path (case-insensitive). Returns -1 when it is not in the index.</summary>
    public int FindByPath(string path)
    {
        string root = Metadata.RootPath.TrimEnd('\\', '/');
        string probe = path.TrimEnd('\\', '/');
        if (probe.Equals(root, StringComparison.OrdinalIgnoreCase)) return RootNode;
        if (!probe.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return -1;

        var rest = probe.AsSpan(root.Length);
        int node = RootNode;
        foreach (var range in rest.SplitAny('\\', '/'))
        {
            var part = rest[range];
            if (part.IsEmpty) continue;
            int next = -1;
            foreach (int child in Children(node))
            {
                if (NameSpan(child).Equals(part, StringComparison.OrdinalIgnoreCase)) { next = child; break; }
            }
            if (next < 0) return -1;
            node = next;
        }
        return node;
    }

    /// <summary>Ancestors from the root down to <paramref name="node"/> (inclusive).</summary>
    public int[] GetAncestry(int node)
    {
        var list = new List<int>();
        for (int n = node; n >= 0; n = n == RootNode ? -1 : parent[n]) list.Add(n);
        list.Reverse();
        return [.. list];
    }

    /// <summary>All nodes below (and including) <paramref name="node"/>, parents before children.</summary>
    public IEnumerable<int> EnumerateSubtree(int node)
    {
        var stack = new Stack<int>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            yield return n;
            var children = Children(n);
            for (int i = children.Length - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    /// <summary>Copies a branch into a new, self-contained index whose root is that branch.</summary>
    public FileIndex Subtree(int node)
    {
        if (node == RootNode) return this;
        var builder = new FileIndexBuilder(Math.Max(16, Math.Min(Count, 1 << 16)));
        int newRoot = builder.AddRoot(GetFullPath(node), modified[node], attributes[node]);

        var queue = new Queue<(int Old, int New)>();
        queue.Enqueue((node, newRoot));
        while (queue.Count > 0)
        {
            var (oldNode, newNode) = queue.Dequeue();
            foreach (int child in Children(oldNode))
            {
                bool isDir = IsDirectory(child);
                int added = builder.Add(newNode, NameSpan(child),
                    isDir ? 0 : size[child], isDir ? 0 : allocated[child],
                    modified[child], attributes[child], flags[child]);
                if (isDir) queue.Enqueue((child, added));
            }
        }

        return builder.Build(Metadata with { RootPath = GetFullPath(node) });
    }

    /// <summary>
    /// A copy without the given nodes (and everything below them), with all totals recomputed.
    /// Used after items are moved to the Recycle Bin so the views update without a rescan.
    /// Node numbers change; look nodes up again by path afterwards.
    /// </summary>
    public FileIndex Without(IEnumerable<int> removed)
    {
        var skip = new bool[Count];
        bool any = false;
        foreach (int n in removed)
        {
            if (n > RootNode && n < Count) { skip[n] = true; any = true; }
        }
        if (!any) return this;

        var builder = new FileIndexBuilder(Math.Max(16, Count));
        int newRoot = builder.AddRoot(Metadata.RootPath, modified[RootNode], attributes[RootNode]);
        var map = new int[Count];
        map[RootNode] = newRoot;
        // Parent(i) < i: one ascending pass sees every parent before its children.
        for (int i = 1; i < Count; i++)
        {
            int p = parent[i];
            if (skip[i] || skip[p] || map[p] < 0) { skip[i] = true; map[i] = -1; continue; }
            bool isDir = IsDirectory(i);
            map[i] = builder.AddUnsynchronized(map[p], NameSpan(i),
                isDir ? 0 : size[i], isDir ? 0 : allocated[i], modified[i], attributes[i], flags[i]);
        }

        long freed = TotalAllocated;
        var result = builder.Build(Metadata);
        freed -= result.TotalAllocated;
        return result.WithMetadata(Metadata with
        {
            VolumeFreeBytes = Metadata.VolumeFreeBytes is { } free ? free + Math.Max(0, freed) : null,
        });
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
