using Boyut.Core.Model;
using Boyut.Core.Scanning;

namespace Boyut.Core.Tests;

public class IncrementalUpdateTests
{
    private static FileIndex Scan(string root) => new ManagedScanner().ScanAsync(root, new ScanOptions()).GetAwaiter().GetResult();

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    /// <summary>Path → size for every node, so two indices can be compared regardless of numbering.</summary>
    private static Dictionary<string, long> Flatten(FileIndex index) =>
        Enumerable.Range(0, index.Count).ToDictionary(index.GetRelativePath, index.Size, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Relisting_changed_folders_matches_a_full_rescan()
    {
        string root = Path.Combine(Path.GetTempPath(), $"boyut-inc-{Guid.NewGuid():N}");
        try
        {
            Write(Path.Combine(root, "a", "keep.bin"), 1000);
            Write(Path.Combine(root, "a", "grow.log"), 100);
            Write(Path.Combine(root, "a", "gone.tmp"), 300);
            Write(Path.Combine(root, "b", "deep", "x.dat"), 5000);
            var old = Scan(root);

            // What the journal would report: a (grew + deleted + new folder) and b\deep (new file).
            Write(Path.Combine(root, "a", "grow.log"), 900);
            File.Delete(Path.Combine(root, "a", "gone.tmp"));
            Write(Path.Combine(root, "a", "new", "inner", "big.iso"), 20_000);
            Write(Path.Combine(root, "b", "deep", "y.dat"), 70);
            string[] changed = [Path.Combine(root, "a"), Path.Combine(root, "b", "deep"), Path.Combine(root, "not", "scanned")];

            var updated = IncrementalUpdate.Apply(old, changed, new ManagedDirectoryLister(new ScanOptions()), old.Metadata);
            var full = Scan(root);

            Assert.Equal(Flatten(full), Flatten(updated));
            Assert.Equal(full.TotalFiles, updated.TotalFiles);
            for (int i = 1; i < updated.Count; i++) Assert.True(updated.Parent(i) < i);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Nothing_changed_keeps_the_same_nodes()
    {
        var old = HistoryTests.Build(DateTime.UtcNow, (@"x\y.txt", 5));
        var meta = old.Metadata with { Usn = new UsnCheckpoint(1, 99) };
        var updated = IncrementalUpdate.Apply(old, [@"D:\elsewhere"], new ManagedDirectoryLister(new ScanOptions()), meta);
        Assert.Equal(old.Count, updated.Count);
        Assert.Equal(new UsnCheckpoint(1, 99), updated.Metadata.Usn);
    }
}
