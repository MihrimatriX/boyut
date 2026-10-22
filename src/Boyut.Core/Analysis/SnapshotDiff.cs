using Boyut.Core.Model;

namespace Boyut.Core.Analysis;

public enum ChangeKind : byte { Changed, Added, Removed }

/// <summary>One path seen in either scan. A side is -1 when the path does not exist in that scan.</summary>
public readonly record struct DiffEntry(int OldNode, int NewNode);

/// <summary>
/// Compares two scans of the same root. Children are matched by name (case-insensitive) only when a folder
/// is expanded, so comparing two full-disk scans costs nothing up front and the UI drills down lazily.
/// </summary>
public sealed class SnapshotDiff
{
    public SnapshotDiff(FileIndex older, FileIndex newer)
    {
        if (!string.Equals(older.Metadata.RootPath.TrimEnd('\\', '/'), newer.Metadata.RootPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("İki tarama aynı klasöre ait değil.");
        Old = older;
        New = newer;
    }

    public FileIndex Old { get; }
    public FileIndex New { get; }

    public static DiffEntry Root => new(FileIndex.RootNode, FileIndex.RootNode);

    public long TotalDelta => New.TotalSize - Old.TotalSize;
    public long FileCountDelta => (long)New.TotalFiles - Old.TotalFiles;
    public long? FreeSpaceDelta => New.Metadata.VolumeFreeBytes - Old.Metadata.VolumeFreeBytes;

    public long OldSize(DiffEntry e) => Size(Old, e.OldNode);
    public long NewSize(DiffEntry e) => Size(New, e.NewNode);
    public long Delta(DiffEntry e) => NewSize(e) - OldSize(e);

    public ChangeKind Kind(DiffEntry e) => e.OldNode < 0 ? ChangeKind.Added : e.NewNode < 0 ? ChangeKind.Removed : ChangeKind.Changed;

    public bool IsDirectory(DiffEntry e) => e.NewNode >= 0 ? New.IsDirectory(e.NewNode) : Old.IsDirectory(e.OldNode);

    public string Name(DiffEntry e) =>
        e.NewNode == FileIndex.RootNode ? New.Metadata.RootPath : e.NewNode >= 0 ? New.GetName(e.NewNode) : Old.GetName(e.OldNode);

    public string FullPath(DiffEntry e) => e.NewNode >= 0 ? New.GetFullPath(e.NewNode) : Old.GetFullPath(e.OldNode);

    /// <summary>Children whose size changed, largest change (either direction) first.</summary>
    public List<DiffEntry> Children(DiffEntry e)
    {
        var result = new List<DiffEntry>();
        bool oldDir = e.OldNode >= 0 && Old.IsDirectory(e.OldNode);
        bool newDir = e.NewNode >= 0 && New.IsDirectory(e.NewNode);

        Dictionary<string, int>? oldByName = null;
        if (oldDir)
        {
            var kids = Old.Children(e.OldNode);
            oldByName = new Dictionary<string, int>(kids.Length, StringComparer.OrdinalIgnoreCase);
            foreach (int k in kids) oldByName.TryAdd(Old.GetName(k), k);
        }

        if (newDir)
        {
            foreach (int k in New.Children(e.NewNode))
            {
                int match = -1;
                if (oldByName is not null && oldByName.Remove(New.GetName(k), out int o))
                {
                    // A file that became a folder (or back) is two unrelated entries.
                    if (Old.IsDirectory(o) == New.IsDirectory(k)) match = o;
                    else result.Add(new DiffEntry(o, -1));
                }
                result.Add(new DiffEntry(match, k));
            }
        }
        if (oldByName is not null)
            foreach (int o in oldByName.Values) result.Add(new DiffEntry(o, -1));

        result.RemoveAll(c => Delta(c) == 0);
        result.Sort((a, b) =>
        {
            int c = Math.Abs(Delta(b)).CompareTo(Math.Abs(Delta(a)));
            return c != 0 ? c : string.Compare(Name(a), Name(b), StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    /// <summary>
    /// Follows the growth down from the root while one child accounts for at least <paramref name="share"/>
    /// of its parent's growth: "C:\ +8 GB → Users → Ali → Downloads". Empty when the root did not grow.
    /// </summary>
    public IReadOnlyList<DiffEntry> GrowthPath(double share = 0.5)
    {
        var path = new List<DiffEntry>();
        var current = Root;
        long delta = Delta(current);
        if (delta <= 0) return path;
        path.Add(current);
        while (IsDirectory(current))
        {
            var top = Children(current).FirstOrDefault(c => Delta(c) > 0);
            if (top == default || Delta(top) < delta * share) break;
            current = top;
            delta = Delta(top);
            path.Add(current);
        }
        return path;
    }

    private static long Size(FileIndex index, int node) =>
        node < 0 ? 0 : node == FileIndex.RootNode ? index.Size(node) : index.EffectiveSize(node);
}
