using Boyut.Core.Analysis;
using Boyut.Core.Model;
using Boyut.Core.Persistence;

namespace Boyut.Core.Tests;

public class HistoryTests
{
    internal static FileIndex Build(DateTime scannedAt, params (string Path, long Size)[] files)
    {
        var b = new FileIndexBuilder();
        int root = b.AddRoot(@"C:\");
        var dirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        foreach (var (path, size) in files)
        {
            var parts = path.Split('\\');
            string prefix = "";
            int parent = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                prefix = prefix.Length == 0 ? parts[i] : prefix + "\\" + parts[i];
                if (!dirs.TryGetValue(prefix, out int d))
                    dirs[prefix] = d = b.Add(parent, parts[i], 0, 0, 0, (int)FileAttributes.Directory, NodeFlags.Directory);
                parent = d;
            }
            b.Add(parent, parts[^1], size, size, 0, 0, NodeFlags.None);
        }
        return b.Build(new ScanMetadata
        {
            RootPath = @"C:\", Separator = '\\', Engine = "test", ScannedAtUtc = scannedAt,
            VolumeFreeBytes = 1_000_000, Usn = new UsnCheckpoint(42, 4096),
        });
    }

    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Header_round_trips_without_loading_arrays()
    {
        var index = Build(T0, (@"Users\Ali\film.mp4", 900), (@"Windows\a.dll", 100));
        string file = Path.Combine(Path.GetTempPath(), $"boyut-test-{Guid.NewGuid():N}.boyut");
        try
        {
            SnapshotSerializer.Save(index, file);
            var header = SnapshotSerializer.ReadHeader(file);
            Assert.Equal(1000, header.TotalSize);
            Assert.Equal(2, header.TotalFiles);
            Assert.Equal(T0, header.Metadata.ScannedAtUtc);
            Assert.Equal(new UsnCheckpoint(42, 4096), header.Metadata.Usn);
            Assert.Equal(new UsnCheckpoint(42, 4096), SnapshotSerializer.Load(file).Metadata.Usn);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void History_keeps_scans_per_root_newest_first_and_prunes()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"boyut-history-{Guid.NewGuid():N}");
        var history = new SnapshotHistory(folder);
        try
        {
            for (int i = 0; i < 12; i++) history.Save(Build(T0.AddDays(i), (@"a.txt", i)));
            var all = history.List(@"C:\");
            Assert.Equal(12, all.Count);
            Assert.Equal(T0.AddDays(11), all[0].ScannedAtUtc);
            Assert.Equal(12, history.ListRoots().Single().Count);

            history.Prune(@"C:\", T0.AddDays(12));
            var kept = history.List(@"C:\");
            Assert.True(kept.Count < 12);
            Assert.Equal(T0.AddDays(11), kept[0].ScannedAtUtc); // newest always survives
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Diff_matches_by_path_and_ranks_by_change()
    {
        var older = Build(T0, (@"Users\Ali\film.mp4", 900), (@"Users\Ali\eski.zip", 50), (@"Windows\a.dll", 100));
        var newer = Build(T0.AddDays(1), (@"users\ali\film.mp4", 900), (@"Users\Ali\Downloads\iso.img", 5000), (@"Windows\a.dll", 130));
        var diff = new SnapshotDiff(older, newer);

        Assert.Equal(4980, diff.TotalDelta);
        var top = diff.Children(SnapshotDiff.Root);
        Assert.Equal(["users", "Windows"], top.Select(diff.Name)); // matched case-insensitively, shown as in the newer scan

        var ali = diff.Children(top[0]).Single();
        var inAli = diff.Children(ali);
        Assert.Equal(["Downloads", "eski.zip"], inAli.Select(diff.Name)); // film.mp4 unchanged → hidden
        Assert.Equal(ChangeKind.Added, diff.Kind(inAli[0]));
        Assert.Equal(ChangeKind.Removed, diff.Kind(inAli[1]));
        Assert.Equal(-50, diff.Delta(inAli[1]));

        Assert.Equal(["C:\\", "users", "ali", "Downloads", "iso.img"], diff.GrowthPath().Select(diff.Name));
    }

    private static FileIndex WithFree(FileIndex i, long free) => i.WithMetadata(i.Metadata with { VolumeFreeBytes = free });

    [Fact]
    public void Low_space_alert_fires_once_when_crossing_the_line()
    {
        var t = new AlertThresholds(MinFreeBytes: 500, GrowthBytes: 0);
        var a = WithFree(Build(T0, (@"x.txt", 1)), 900);
        var b = WithFree(Build(T0.AddHours(1), (@"x.txt", 1), (@"Temp\y.tmp", 600)), 300);
        var c = WithFree(Build(T0.AddHours(2), (@"x.txt", 1), (@"Temp\y.tmp", 650)), 250);

        var crossed = SpaceAlerts.Evaluate(b, new SnapshotDiff(a, b), t);
        Assert.NotNull(crossed);
        Assert.Contains("yer azaldı", crossed.Title);
        Assert.Contains(@"C:\Temp\y.tmp", crossed.Message);

        Assert.Null(SpaceAlerts.Evaluate(c, new SnapshotDiff(b, c), t)); // already below: stay quiet
        Assert.NotNull(SpaceAlerts.Evaluate(b, null, t));                // first scan already low: tell once
    }

    [Fact]
    public void Growth_alert_needs_the_threshold()
    {
        var a = WithFree(Build(T0, (@"x.txt", 1)), 1_000_000);
        var b = WithFree(Build(T0.AddDays(1), (@"x.txt", 1), (@"Downloads\big.iso", 5000)), 995_000);
        Assert.Null(SpaceAlerts.Evaluate(b, new SnapshotDiff(a, b), new AlertThresholds(100, 10_000)));
        var alert = SpaceAlerts.Evaluate(b, new SnapshotDiff(a, b), new AlertThresholds(100, 4000));
        Assert.NotNull(alert);
        Assert.Contains("büyüdü", alert.Title);
    }

    [Fact]
    public void Diff_refuses_different_roots()
    {
        var a = Build(T0, (@"x.txt", 1));
        var b = a.WithMetadata(a.Metadata with { RootPath = @"D:\" });
        Assert.Throws<ArgumentException>(() => new SnapshotDiff(a, b));
    }
}
