using Boyut.Core.Formatting;
using Boyut.Core.Model;
using Boyut.Core.Persistence;
using Boyut.Core.Scanning;

namespace Boyut.Core.Tests;

public class FileIndexTests
{
    internal static FileIndex Sample()
    {
        // Added out of order on purpose: the builder must cope with children before parents.
        var b = new FileIndexBuilder();
        int root = b.AddRoot(@"C:\");
        int users = b.Add(root, "Users", 0, 0, 0, (int)FileAttributes.Directory, NodeFlags.Directory);
        int video = b.Add(FileIndexBuilder.UnknownParent, "film.mp4", 900, 1024, Ticks(2026, 9, 14), 0, NodeFlags.None);
        int ali = b.Add(users, "Ali", 0, 0, 0, (int)FileAttributes.Directory, NodeFlags.Directory);
        b.SetParent(video, ali);
        b.Add(ali, "notlar.txt", 100, 4096, Ticks(2024, 1, 2), (int)FileAttributes.Hidden, NodeFlags.None);
        b.Add(ali, "notlar-link.txt", 100, 4096, Ticks(2024, 1, 2), 0, NodeFlags.HardLinkSecondary);
        int win = b.Add(root, "Windows", 0, 0, 0, (int)(FileAttributes.Directory | FileAttributes.System), NodeFlags.Directory);
        b.Add(win, "pagefile.sys", 5000, 5000, Ticks(2026, 10, 20), (int)(FileAttributes.Hidden | FileAttributes.System), NodeFlags.None);
        b.Add(12345, "orphan.bin", 77, 77, 0, 0, NodeFlags.None); // parent does not exist
        return b.Build(new ScanMetadata { RootPath = @"C:\", Separator = '\\', Engine = "test" });
    }

    internal static long Ticks(int y, int m, int d) => new DateTime(y, m, d, 12, 0, 0, DateTimeKind.Utc).Ticks;

    [Fact]
    public void Aggregates_and_drops_orphans()
    {
        var index = Sample();
        Assert.Equal(8, index.Count); // root + 7, orphan dropped
        Assert.Equal(6000, index.TotalSize); // hard link counted once
        Assert.Equal(4, index.TotalFiles);
        Assert.Equal(-1, index.FindByPath(@"C:\orphan.bin"));
    }

    [Fact]
    public void Breadth_first_numbering_keeps_parents_first()
    {
        var index = Sample();
        for (int i = 1; i < index.Count; i++) Assert.True(index.Parent(i) < i);
    }

    [Fact]
    public void Children_sorted_largest_first()
    {
        var index = Sample();
        var top = index.Children(FileIndex.RootNode).ToArray().Select(index.GetName).ToArray();
        Assert.Equal(["Windows", "Users"], top);
    }

    [Fact]
    public void Full_paths_with_drive_root()
    {
        var index = Sample();
        int film = index.FindByPath(@"c:\users\ALI\film.mp4");
        Assert.True(film > 0);
        Assert.Equal(@"C:\Users\Ali\film.mp4", index.GetFullPath(film));
        Assert.Equal(@"Users\Ali\film.mp4", index.GetRelativePath(film));
        Assert.Equal(@"C:\", index.GetFullPath(FileIndex.RootNode));
        Assert.Equal(3, index.Depth(film));
        Assert.True(index.IsAncestorOrSelf(index.FindByPath(@"C:\Users"), film));
        Assert.False(index.IsAncestorOrSelf(index.FindByPath(@"C:\Windows"), film));
    }

    [Fact]
    public void Full_paths_with_unix_root()
    {
        var b = new FileIndexBuilder();
        int root = b.AddRoot("/");
        int home = b.Add(root, "home", 0, 0, 0, 0, NodeFlags.Directory);
        int f = b.Add(home, "a.txt", 1, 1, 0, 0, NodeFlags.None);
        var index = b.Build(new ScanMetadata { RootPath = "/", Separator = '/', Engine = "test" });
        Assert.Equal("/home/a.txt", index.GetFullPath(index.FindByPath("/home/a.txt")));
        Assert.Equal("/home", index.GetFullPath(index.FindByPath("/home/")));
        _ = f;
    }

    [Fact]
    public void Extension_rules()
    {
        var index = Sample();
        Assert.Equal("mp4", index.ExtensionSpan(index.FindByPath(@"C:\Users\Ali\film.mp4")).ToString());
        Assert.True(index.ExtensionSpan(index.FindByPath(@"C:\Users")).IsEmpty);
    }

    [Fact]
    public void Without_removes_branches_and_recomputes_totals()
    {
        var index = Sample();
        int ali = index.FindByPath(@"C:\Users\Ali");
        int page = index.FindByPath(@"C:\Windows\pagefile.sys");
        var smaller = index.Without([ali, page]);

        Assert.Equal(3, smaller.Count); // root, Users, Windows
        Assert.Equal(0, smaller.TotalSize);
        Assert.Equal(-1, smaller.FindByPath(@"C:\Users\Ali\film.mp4"));
        Assert.True(smaller.FindByPath(@"C:\Users") > 0);
        Assert.Same(index, index.Without([]));
    }

    [Fact]
    public void Snapshot_round_trip()
    {
        var index = Sample();
        using var ms = new MemoryStream();
        SnapshotSerializer.Save(index, ms);
        ms.Position = 0;
        var copy = SnapshotSerializer.Load(ms);

        Assert.Equal(index.Count, copy.Count);
        Assert.Equal(index.TotalSize, copy.TotalSize);
        Assert.Equal(index.Metadata.RootPath, copy.Metadata.RootPath);
        for (int i = 0; i < index.Count; i++)
        {
            Assert.Equal(index.GetFullPath(i), copy.GetFullPath(i));
            Assert.Equal(index.Size(i), copy.Size(i));
            Assert.Equal(index.Flags(i), copy.Flags(i));
            Assert.Equal(index.Children(i).ToArray(), copy.Children(i).ToArray());
        }
    }

    [Fact]
    public void Snapshot_rejects_garbage()
    {
        using var ms = new MemoryStream("not a snapshot at all"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => SnapshotSerializer.Load(ms));
    }

    [Fact]
    public void Csv_export_quotes_and_lists_everything()
    {
        var index = Sample();
        var writer = new StringWriter();
        CsvExporter.ExportTree(index, writer, culture: System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(index.Count + 1, lines.Length);
        Assert.StartsWith("Yol;Ad;", lines[0]);
        Assert.Contains(lines, l => l.StartsWith(@"C:\Windows\pagefile.sys;pagefile.sys;Dosya;5000;"));
    }

    [Theory]
    [InlineData(0, "0 bayt")]
    [InlineData(1023, "1.023 bayt")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(536_870_912, "512 MB")]
    [InlineData(466_646_958_489, "434,6 GB")]
    public void Formats_sizes_in_turkish(long bytes, string expected) =>
        Assert.Equal(expected, SizeFormatter.Format(bytes));

    [Theory]
    [InlineData("500", 500L << 20)]
    [InlineData("500mb", 500L << 20)]
    [InlineData("1,5gb", 3L << 29)]
    [InlineData("1.5 GB", 3L << 29)]
    [InlineData("10k", 10L << 10)]
    [InlineData("2tb", 2L << 40)]
    [InlineData("12b", 12L)]
    public void Parses_sizes(string text, long expected)
    {
        Assert.True(SizeFormatter.TryParse(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("5xb")]
    public void Rejects_bad_sizes(string text) => Assert.False(SizeFormatter.TryParse(text, out _));
}

public class ManagedScannerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "boyut-test-" + Guid.NewGuid().ToString("N"));

    public ManagedScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllBytes(Path.Combine(root, "a", "b", "x.bin"), new byte[10_000]);
        File.WriteAllBytes(Path.Combine(root, "a", "y.bin"), new byte[3]);
        File.WriteAllText(Path.Combine(root, "z.txt"), "merhaba");
        for (int i = 0; i < 500; i++) File.WriteAllText(Path.Combine(root, "a", $"f{i:000}.txt"), "1");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Scans_tree_with_totals()
    {
        var progress = new List<ScanProgress>();
        var index = await new ManagedScanner(new ClusterRoundingAllocation(4096))
            .ScanAsync(root, new ScanOptions { MaxDegreeOfParallelism = 4 }, new Progress<ScanProgress>(progress.Add));

        Assert.Equal(10_000 + 3 + 7 + 500, index.TotalSize);
        Assert.Equal(503, index.TotalFiles);
        Assert.Equal(root, index.Metadata.RootPath);
        Assert.Equal("managed", index.Metadata.Engine);

        int x = index.FindByPath(Path.Combine(root, "a", "b", "x.bin"));
        Assert.True(x > 0);
        Assert.Equal(12_288, index.Allocated(x)); // rounded to 3 clusters
        Assert.Equal(Path.Combine(root, "a", "b", "x.bin"), index.GetFullPath(x));

        int empty = index.FindByPath(Path.Combine(root, "empty"));
        Assert.True(index.IsDirectory(empty));
        Assert.Equal(0, index.Size(empty));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ManagedScanner().ScanAsync(root, new ScanOptions(), null, cts.Token));
    }

    [Fact]
    public async Task Missing_root_throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            new ManagedScanner().ScanAsync(Path.Combine(root, "nope"), new ScanOptions()));
    }
}
