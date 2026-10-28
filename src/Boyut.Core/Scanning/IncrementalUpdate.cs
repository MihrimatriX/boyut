using System.IO.Enumeration;
using Boyut.Core.Model;

namespace Boyut.Core.Scanning;

/// <summary>One entry of a directory listing.</summary>
public readonly record struct ListedEntry(string Name, long Size, long Allocated, long ModifiedTicks, int Attributes, NodeFlags Flags)
{
    public bool IsDirectory => (Flags & NodeFlags.Directory) != 0;
}

/// <summary>Reads the file system for <see cref="IncrementalUpdate"/>; swapped for a fake in tests.</summary>
public interface IDirectoryLister
{
    /// <summary>Direct children of a folder, or null when it is gone or unreadable.</summary>
    IReadOnlyList<ListedEntry>? List(string path);

    /// <summary>Full scan of a folder that did not exist in the old index.</summary>
    FileIndex ScanTree(string path);
}

/// <summary>
/// Brings an old index up to date by listing only the folders that changed (from the NTFS change journal):
/// unchanged folders keep their old subtree, changed folders get a fresh listing, new folders a full scan.
/// </summary>
public static class IncrementalUpdate
{
    public static FileIndex Apply(FileIndex old, IEnumerable<string> changedDirectories, IDirectoryLister lister, ScanMetadata metadata)
    {
        var changed = new HashSet<int>();
        foreach (string path in changedDirectories)
        {
            int node = old.FindByPath(path);
            if (node >= 0 && old.IsDirectory(node)) changed.Add(node);
        }
        if (changed.Count == 0) return old.WithMetadata(metadata);

        var builder = new FileIndexBuilder(old.Count + 1024);
        builder.AddRoot(old.Metadata.RootPath, old.ModifiedTicks(FileIndex.RootNode), old.RawAttributes(FileIndex.RootNode));
        var queue = new Queue<(int Old, int New)>();
        queue.Enqueue((FileIndex.RootNode, FileIndex.RootNode));

        while (queue.Count > 0)
        {
            var (o, n) = queue.Dequeue();
            var listing = changed.Contains(o) ? lister.List(old.GetFullPath(o)) : null;
            if (listing is null)
            {
                foreach (int child in old.Children(o)) CopyNode(old, child, n, builder, queue);
                continue;
            }

            var oldByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (int child in old.Children(o)) oldByName.TryAdd(old.GetName(child), child);

            foreach (var entry in listing)
            {
                bool existed = oldByName.TryGetValue(entry.Name, out int before) && old.IsDirectory(before) == entry.IsDirectory;
                if (!entry.IsDirectory)
                {
                    // A relisted hard link still must not be counted twice; the old scan knew which name was secondary.
                    var flags = entry.Flags | (existed ? old.Flags(before) & NodeFlags.HardLinkSecondary : NodeFlags.None);
                    builder.AddUnsynchronized(n, entry.Name, entry.Size, entry.Allocated, entry.ModifiedTicks, entry.Attributes, flags);
                }
                else if (existed)
                {
                    int added = builder.AddUnsynchronized(n, entry.Name, 0, 0, entry.ModifiedTicks, entry.Attributes,
                        entry.Flags | (old.Flags(before) & NodeFlags.AccessDenied));
                    queue.Enqueue((before, added));
                }
                else
                {
                    int added = builder.AddUnsynchronized(n, entry.Name, 0, 0, entry.ModifiedTicks, entry.Attributes, entry.Flags);
                    // ponytail: new junctions stay leaves (as with FollowReparsePoints off); a full rescan follows them.
                    if ((entry.Flags & NodeFlags.ReparsePoint) == 0)
                        Graft(lister.ScanTree(Path.Join(old.GetFullPath(o), entry.Name)), added, builder);
                }
            }
        }
        return builder.Build(metadata);
    }

    private static void CopyNode(FileIndex old, int child, int newParent, FileIndexBuilder builder, Queue<(int, int)> queue)
    {
        bool isDir = old.IsDirectory(child);
        int added = builder.AddUnsynchronized(newParent, old.NameSpan(child),
            isDir ? 0 : old.Size(child), isDir ? 0 : old.Allocated(child),
            old.ModifiedTicks(child), old.RawAttributes(child), old.Flags(child));
        if (isDir) queue.Enqueue((child, added));
    }

    /// <summary>Copies everything below <paramref name="sub"/>'s root under <paramref name="parent"/>.</summary>
    private static void Graft(FileIndex sub, int parent, FileIndexBuilder builder)
    {
        var queue = new Queue<(int, int)>();
        foreach (int child in sub.Children(FileIndex.RootNode)) CopyNode(sub, child, parent, builder, queue);
        while (queue.Count > 0)
        {
            var (o, n) = queue.Dequeue();
            foreach (int child in sub.Children(o)) CopyNode(sub, child, n, builder, queue);
        }
    }
}

/// <summary>Lists folders through <see cref="FileSystemEnumerable{TResult}"/>, the same way <see cref="ManagedScanner"/> does.</summary>
public sealed class ManagedDirectoryLister(ScanOptions options, IAllocationSizeProvider? allocation = null) : IDirectoryLister
{
    private readonly IAllocationSizeProvider allocation = allocation ?? new ClusterRoundingAllocation();

    public IReadOnlyList<ListedEntry>? List(string path)
    {
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = options.IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
        };
        try
        {
            return new FileSystemEnumerable<ListedEntry>(path, (ref FileSystemEntry e) =>
            {
                int attributes = (int)e.Attributes;
                var flags = e.IsDirectory ? NodeFlags.Directory : NodeFlags.None;
                if ((attributes & (int)FileAttributes.ReparsePoint) != 0) flags |= NodeFlags.ReparsePoint;
                long length = 0, allocated = 0;
                if (!e.IsDirectory)
                {
                    length = e.Length;
                    if (ExtraFileAttributes.IsCloudPlaceholder(attributes)) flags |= NodeFlags.CloudPlaceholder;
                    allocated = allocation.GetAllocatedSize(e.ToFullPath(), length, attributes);
                }
                long modified;
                try { modified = e.LastWriteTimeUtc.UtcTicks; } catch (ArgumentOutOfRangeException) { modified = 0; }
                return new ListedEntry(e.FileName.ToString(), length, allocated, modified, attributes, flags);
            }, enumeration).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public FileIndex ScanTree(string path) =>
        new ManagedScanner(allocation).ScanAsync(path, options).GetAwaiter().GetResult();
}
