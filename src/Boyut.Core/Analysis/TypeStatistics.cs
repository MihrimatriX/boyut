using Boyut.Core.Model;

namespace Boyut.Core.Analysis;

public sealed record CategoryTotal(FileCategory Category, long Bytes, long Allocated, int Files)
{
    public string Name => FileCategories.DisplayName(Category);
}

public sealed record ExtensionTotal(string Extension, FileCategory Category, long Bytes, int Files);

public sealed record TypeBreakdown(IReadOnlyList<CategoryTotal> Categories, IReadOnlyList<ExtensionTotal> Extensions, long TotalBytes, int TotalFiles);

public static class TypeStatistics
{
    public static TypeBreakdown Compute(FileIndex index, int scope = FileIndex.RootNode, int topExtensions = 50)
    {
        var categories = new (long Bytes, long Allocated, int Files)[Enum.GetValues<FileCategory>().Length];
        var extensions = new Dictionary<string, (long Bytes, int Files)>(StringComparer.OrdinalIgnoreCase);
        var lookup = extensions.GetAlternateLookup<ReadOnlySpan<char>>();
        long totalBytes = 0;
        int totalFiles = 0;

        foreach (int node in index.EnumerateSubtree(scope))
        {
            if (index.IsDirectory(node)) continue;
            long size = index.EffectiveSize(node);
            var ext = index.ExtensionSpan(node);
            var cat = FileCategories.Classify(ext);

            ref var c = ref categories[(int)cat];
            c.Bytes += size;
            c.Allocated += (index.Flags(node) & NodeFlags.HardLinkSecondary) != 0 ? 0 : index.Allocated(node);
            c.Files++;

            var key = ext.IsEmpty ? "(uzantısız)".AsSpan() : ext;
            lookup.TryGetValue(key, out var e);
            lookup[key] = (e.Bytes + size, e.Files + 1);

            totalBytes += size;
            totalFiles++;
        }

        var categoryList = categories
            .Select((c, i) => new CategoryTotal((FileCategory)i, c.Bytes, c.Allocated, c.Files))
            .Where(c => c.Files > 0)
            .OrderByDescending(c => c.Bytes)
            .ToList();

        var extensionList = extensions
            .Select(kv => new ExtensionTotal(kv.Key, FileCategories.Classify(kv.Key), kv.Value.Bytes, kv.Value.Files))
            .OrderByDescending(e => e.Bytes)
            .Take(topExtensions)
            .ToList();

        return new TypeBreakdown(categoryList, extensionList, totalBytes, totalFiles);
    }

    /// <summary>The N largest files below <paramref name="scope"/>.</summary>
    public static int[] LargestFiles(FileIndex index, int scope = FileIndex.RootNode, int count = 50)
    {
        var heap = new PriorityQueue<int, long>(count + 1);
        foreach (int node in index.EnumerateSubtree(scope))
        {
            if (index.IsDirectory(node) || (index.Flags(node) & NodeFlags.HardLinkSecondary) != 0) continue;
            heap.Enqueue(node, index.Size(node));
            if (heap.Count > count) heap.Dequeue();
        }
        var result = new int[heap.Count];
        for (int i = result.Length - 1; i >= 0; i--) result[i] = heap.Dequeue();
        return result;
    }

    /// <summary>Bytes per age bucket (by last modification).</summary>
    public static IReadOnlyList<(string Label, long Bytes, int Files)> AgeDistribution(FileIndex index, DateTime nowUtc, int scope = FileIndex.RootNode)
    {
        (string Label, TimeSpan MaxAge)[] buckets =
        [
            ("Son 7 gün", TimeSpan.FromDays(7)),
            ("Son 30 gün", TimeSpan.FromDays(30)),
            ("Son 6 ay", TimeSpan.FromDays(182)),
            ("Son 1 yıl", TimeSpan.FromDays(365)),
            ("1–3 yıl", TimeSpan.FromDays(3 * 365)),
            ("3 yıldan eski", TimeSpan.MaxValue),
        ];
        var totals = new (long Bytes, int Files)[buckets.Length];
        foreach (int node in index.EnumerateSubtree(scope))
        {
            if (index.IsDirectory(node)) continue;
            long ticks = index.ModifiedTicks(node);
            var age = ticks > 0 ? nowUtc - new DateTime(ticks, DateTimeKind.Utc) : TimeSpan.MaxValue;
            int b = 0;
            while (b < buckets.Length - 1 && age > buckets[b].MaxAge) b++;
            totals[b].Bytes += index.EffectiveSize(node);
            totals[b].Files++;
        }
        return buckets.Select((bk, i) => (bk.Label, totals[i].Bytes, totals[i].Files)).ToList();
    }
}
