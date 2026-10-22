using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;
using Boyut.Core.Model;

namespace Boyut.Core.Analysis;

public sealed record DuplicateGroup(long Size, int[] Nodes, UInt128 Hash)
{
    /// <summary>Space freed by keeping one copy.</summary>
    public long Wasted => Size * (Nodes.Length - 1);
}

public readonly record struct DuplicateProgress(string Phase, long Done, long Total);

public sealed record DuplicateOptions
{
    public long MinimumSize { get; init; } = 1 << 20;
    /// <summary>Skip hidden/system files and the Windows folder.</summary>
    public bool SkipSystem { get; init; } = true;
    public int MaxDegreeOfParallelism { get; init; } = 4;

    /// <summary>
    /// Optional "same physical file?" check (volume serial + file id). Two names of one
    /// hard-linked file are not duplicates: deleting one frees nothing.
    /// </summary>
    public Func<string, (ulong Volume, ulong FileId)?>? IdentityProvider { get; init; }
}

/// <summary>
/// Content-based duplicate detection in three passes, each one only on the survivors of the last:
/// same size → same hash of the first and last 64 KB → same XxHash128 of the whole file.
/// Never opens cloud placeholders (that would download them).
/// </summary>
public static class DuplicateFinder
{
    private const int EdgeBytes = 64 * 1024;

    public static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(FileIndex index, int scope = FileIndex.RootNode,
        DuplicateOptions? options = null, IProgress<DuplicateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new DuplicateOptions();
        return await Task.Run(() => Find(index, scope, options, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static List<DuplicateGroup> Find(FileIndex index, int scope, DuplicateOptions options,
        IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        // Pass 1: group by size.
        progress?.Report(new DuplicateProgress("Boyuta göre gruplanıyor", 0, 0));
        var bySize = new Dictionary<long, List<int>>();
        const NodeFlags skipFlags = NodeFlags.HardLinkSecondary | NodeFlags.CloudPlaceholder | NodeFlags.ReparsePoint;
        const FileAttributes systemAttrs = FileAttributes.System | FileAttributes.Offline;

        var stack = new Stack<int>();
        stack.Push(scope);
        while (stack.Count > 0)
        {
            int node = stack.Pop();
            if (index.IsDirectory(node))
            {
                if (options.SkipSystem && node != scope && index.Parent(node) == FileIndex.RootNode &&
                    index.NameSpan(node).Equals("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (int c in index.Children(node)) stack.Push(c);
                continue;
            }
            long size = index.Size(node);
            if (size < options.MinimumSize) continue;
            if ((index.Flags(node) & skipFlags) != 0) continue;
            if (options.SkipSystem && (index.Attributes(node) & systemAttrs) != 0) continue;

            if (!bySize.TryGetValue(size, out var list)) bySize[size] = list = new List<int>(2);
            list.Add(node);
        }

        var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
        long totalFiles = candidates.Sum(kv => (long)kv.Value.Count);

        // Pass 2: hash of the edges.
        long done = 0;
        var edgeGroups = new ConcurrentBag<(long Size, int[] Nodes)>();
        Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, kv =>
        {
            var groups = new Dictionary<UInt128, List<int>>();
            foreach (int node in DistinctPhysical(index, kv.Value, options))
            {
                var hash = HashEdges(index.GetFullPath(node), kv.Key);
                if (hash is { } h)
                {
                    if (!groups.TryGetValue(h, out var g)) groups[h] = g = new List<int>(2);
                    g.Add(node);
                }
                long n = Interlocked.Increment(ref done);
                if ((n & 63) == 0) progress?.Report(new DuplicateProgress("Dosya başları karşılaştırılıyor", n, totalFiles));
            }
            foreach (var g in groups.Values)
                if (g.Count > 1) edgeGroups.Add((kv.Key, g.ToArray()));
        });

        // Pass 3: full content hash (small files were already read whole in pass 2).
        var result = new ConcurrentBag<DuplicateGroup>();
        long fullTotal = edgeGroups.Sum(g => (long)g.Nodes.Length);
        done = 0;
        Parallel.ForEach(edgeGroups, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, group =>
        {
            if (group.Size <= 2 * EdgeBytes)
            {
                var edge = HashEdges(index.GetFullPath(group.Nodes[0]), group.Size) ?? default;
                result.Add(new DuplicateGroup(group.Size, group.Nodes, edge));
                Interlocked.Add(ref done, group.Nodes.Length);
                return;
            }

            var byHash = new Dictionary<UInt128, List<int>>();
            foreach (int node in group.Nodes)
            {
                ct.ThrowIfCancellationRequested();
                var hash = HashFull(index.GetFullPath(node), ct);
                if (hash is { } h)
                {
                    if (!byHash.TryGetValue(h, out var list)) byHash[h] = list = new List<int>(2);
                    list.Add(node);
                }
                long n = Interlocked.Increment(ref done);
                progress?.Report(new DuplicateProgress("İçerik karşılaştırılıyor", n, fullTotal));
            }
            foreach (var (hash, nodes) in byHash)
                if (nodes.Count > 1) result.Add(new DuplicateGroup(group.Size, nodes.ToArray(), hash));
        });

        return result.OrderByDescending(g => g.Wasted).ToList();
    }

    private static IEnumerable<int> DistinctPhysical(FileIndex index, List<int> nodes, DuplicateOptions options)
    {
        if (options.IdentityProvider is null) return nodes;
        var seen = new HashSet<(ulong, ulong)>();
        var result = new List<int>(nodes.Count);
        foreach (int node in nodes)
        {
            var id = options.IdentityProvider(index.GetFullPath(node));
            if (id is null || seen.Add(id.Value)) result.Add(node);
        }
        return result;
    }

    private static FileStream? Open(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static UInt128? HashEdges(string path, long size)
    {
        using var stream = Open(path);
        if (stream is null) return null;
        try
        {
            var hasher = new XxHash128();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(EdgeBytes);
            try
            {
                if (size <= 2 * EdgeBytes)
                {
                    int read;
                    while ((read = stream.Read(buffer, 0, EdgeBytes)) > 0) hasher.Append(buffer.AsSpan(0, read));
                }
                else
                {
                    stream.ReadExactly(buffer, 0, EdgeBytes);
                    hasher.Append(buffer.AsSpan(0, EdgeBytes));
                    stream.Seek(-EdgeBytes, SeekOrigin.End);
                    stream.ReadExactly(buffer, 0, EdgeBytes);
                    hasher.Append(buffer.AsSpan(0, EdgeBytes));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return hasher.GetCurrentHashAsUInt128();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static UInt128? HashFull(string path, CancellationToken ct)
    {
        using var stream = Open(path);
        if (stream is null) return null;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            var hasher = new XxHash128();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.Append(buffer.AsSpan(0, read));
            }
            return hasher.GetCurrentHashAsUInt128();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
