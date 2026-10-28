using System.Collections.Concurrent;
using System.Diagnostics;
using Boyut.Core.Model;

namespace Boyut.Core.Search;

public sealed record SearchResult(
    int[] Nodes,
    int TotalMatches,
    long TotalBytes,
    bool Truncated,
    TimeSpan Elapsed)
{
    public static SearchResult Empty { get; } = new([], 0, 0, false, TimeSpan.Zero);
}

/// <summary>Parallel, allocation-free scan of the in-memory index.</summary>
public static class SearchEngine
{
    /// <param name="scope">Only look below this node (the root searches everything).</param>
    /// <param name="maxResults">Largest matches kept; the totals still cover every match.</param>
    public static SearchResult Search(FileIndex index, Query query, int scope = FileIndex.RootNode,
        int maxResults = 10_000, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!query.IsValid) return SearchResult.Empty with { Elapsed = stopwatch.Elapsed };

        var bag = new ConcurrentBag<List<int>>();
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };

        if (scope == FileIndex.RootNode)
        {
            var ranges = System.Collections.Concurrent.Partitioner.Create(1, index.Count, 16_384);
            Parallel.ForEach(ranges, options, () => new List<int>(),
                (range, _, local) =>
                {
                    var path = new PathBuffer();
                    for (int node = range.Item1; node < range.Item2; node++)
                        if (query.IsMatch(index, node, ref path)) local.Add(node);
                    cancellationToken.ThrowIfCancellationRequested();
                    return local;
                },
                bag.Add);
        }
        else
        {
            var nodes = index.EnumerateSubtree(scope).Skip(1).ToArray();
            var ranges = System.Collections.Concurrent.Partitioner.Create(0, nodes.Length, 16_384);
            Parallel.ForEach(ranges, options, () => new List<int>(),
                (range, _, local) =>
                {
                    var path = new PathBuffer();
                    for (int i = range.Item1; i < range.Item2; i++)
                        if (query.IsMatch(index, nodes[i], ref path)) local.Add(nodes[i]);
                    cancellationToken.ThrowIfCancellationRequested();
                    return local;
                },
                bag.Add);
        }

        int total = bag.Sum(l => l.Count);
        var all = new int[total];
        int pos = 0;
        foreach (var list in bag) { list.CopyTo(all, pos); pos += list.Count; }

        long bytes = NonOverlappingTotal(index, all);

        // Largest first; ties by node number (= breadth-first order) for a stable list.
        Array.Sort(all, (a, b) =>
        {
            int c = index.Size(b).CompareTo(index.Size(a));
            return c != 0 ? c : a.CompareTo(b);
        });

        bool truncated = all.Length > maxResults;
        if (truncated) Array.Resize(ref all, maxResults);
        return new SearchResult(all, total, bytes, truncated, stopwatch.Elapsed);
    }

    /// <summary>
    /// Sum of sizes where a match inside another matching folder is not counted again
    /// (a "node_modules" folder and the files in it are the same bytes).
    /// </summary>
    public static long NonOverlappingTotal(FileIndex index, IReadOnlyCollection<int> nodes)
    {
        if (nodes.Count == 0) return 0;
        var marked = new bool[index.Count];
        foreach (int n in nodes) marked[n] = true;

        long total = 0;
        foreach (int n in nodes)
        {
            bool covered = false;
            for (int p = index.Parent(n); p >= 0; p = p == FileIndex.RootNode ? -1 : index.Parent(p))
            {
                if (marked[p]) { covered = true; break; }
            }
            if (!covered) total += index.EffectiveSize(n);
        }
        return total;
    }
}
