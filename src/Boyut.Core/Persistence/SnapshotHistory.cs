using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Boyut.Core.Model;

namespace Boyut.Core.Persistence;

public sealed record HistoryEntry(string File, SnapshotHeader Header)
{
    public string RootPath => Header.Metadata.RootPath;
    public DateTime ScannedAtUtc => Header.Metadata.ScannedAtUtc;
}

/// <summary>
/// Every scan of a root, kept as <c>&lt;folder&gt;\&lt;root key&gt;\yyyyMMdd-HHmmss.boyut</c> so two scans can be
/// compared later. Shared by the app, the background monitor and the CLI; the files are the only state.
/// </summary>
public sealed class SnapshotHistory(string folder)
{
    private const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>%LOCALAPPDATA%\Boyut\Taramalar — where the app, the background monitor and the CLI keep scans.</summary>
    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Boyut", "Taramalar");

    public string Folder => folder;

    /// <summary>Folder name for a root: readable part + hash, e.g. <c>C-1a2b3c4d</c>.</summary>
    public static string KeyFor(string root)
    {
        root = root.TrimEnd('\\', '/');
        string name = string.Concat(root.Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_');
        if (name.Length == 0) name = "kok";
        if (name.Length > 80) name = name[..80];
        uint hash = XxHash32.HashToUInt32(Encoding.Unicode.GetBytes(root.ToUpperInvariant()));
        return $"{name}-{hash:x8}";
    }

    public string Save(FileIndex index)
    {
        string dir = Path.Combine(folder, KeyFor(index.Metadata.RootPath));
        Directory.CreateDirectory(dir);
        string file = UniquePath(dir, index.Metadata.ScannedAtUtc);
        SnapshotSerializer.Save(index, file);
        return file;
    }

    /// <summary>Moves a snapshot that lives elsewhere (e.g. the pre-history single file per root) into the history.</summary>
    public string Import(string file)
    {
        var header = SnapshotSerializer.ReadHeader(file);
        string dir = Path.Combine(folder, KeyFor(header.Metadata.RootPath));
        Directory.CreateDirectory(dir);
        string target = UniquePath(dir, header.Metadata.ScannedAtUtc);
        File.Move(file, target);
        return target;
    }

    /// <summary>Scans of one root, newest first. Unreadable files are skipped.</summary>
    public IReadOnlyList<HistoryEntry> List(string root) => ListFolder(Path.Combine(folder, KeyFor(root)));

    /// <summary>The newest scan of every root, newest first.</summary>
    public IReadOnlyList<(HistoryEntry Latest, int Count)> ListRoots()
    {
        if (!Directory.Exists(folder)) return [];
        var result = new List<(HistoryEntry, int)>();
        foreach (string dir in Directory.EnumerateDirectories(folder))
        {
            var entries = ListFolder(dir);
            if (entries.Count > 0) result.Add((entries[0], entries.Count));
        }
        return [.. result.OrderByDescending(r => r.Item1.ScannedAtUtc)];
    }

    /// <summary>Deletes the scans the retention policy no longer wants. Returns how many were removed.</summary>
    public int Prune(string root, DateTime nowUtc)
    {
        var entries = List(root);
        int removed = 0;
        foreach (int i in SnapshotRetention.SelectToDelete([.. entries.Select(e => e.ScannedAtUtc)], nowUtc).Distinct())
        {
            if (i <= 0 || i >= entries.Count) continue; // never the newest
            if (TryDelete(entries[i].File)) removed++;
        }
        return removed;
    }

    public void Forget(string root)
    {
        string dir = Path.Combine(folder, KeyFor(root));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void ForgetAll()
    {
        if (!Directory.Exists(folder)) return;
        foreach (string dir in Directory.EnumerateDirectories(folder))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static IReadOnlyList<HistoryEntry> ListFolder(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        var list = new List<HistoryEntry>();
        foreach (string file in Directory.EnumerateFiles(dir, "*" + SnapshotSerializer.FileExtension))
        {
            try { list.Add(new HistoryEntry(file, SnapshotSerializer.ReadHeader(file))); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException) { }
        }
        list.Sort((a, b) => b.ScannedAtUtc.CompareTo(a.ScannedAtUtc));
        return list;
    }

    private static string UniquePath(string dir, DateTime scannedAtUtc)
    {
        string stamp = scannedAtUtc.ToString(StampFormat, CultureInfo.InvariantCulture);
        string file = Path.Combine(dir, stamp + SnapshotSerializer.FileExtension);
        for (int n = 2; File.Exists(file); n++)
            file = Path.Combine(dir, $"{stamp}-{n}{SnapshotSerializer.FileExtension}");
        return file;
    }

    private static bool TryDelete(string file)
    {
        try { File.Delete(file); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>Which old scans to drop. Pure function so the policy can be tested without files.</summary>
public static class SnapshotRetention
{
    /// <param name="scansNewestFirst">Scan times of one root, newest first.</param>
    /// <returns>Indices into <paramref name="scansNewestFirst"/> to delete. Index 0 (the newest) is never deleted.</returns>
    public static IEnumerable<int> SelectToDelete(IReadOnlyList<DateTime> scansNewestFirst, DateTime nowUtc)
    {
        // TODO(human): replace this placeholder ("keep the newest 10") with a policy that still answers
        // "what grew since last week / last month?" when the background monitor scans every hour.
        // A scan is ~15–25 MB for a full C: drive. Return indices to delete; never 0 (the newest).
        const int keep = 10;
        for (int i = keep; i < scansNewestFirst.Count; i++) yield return i;
    }
}
