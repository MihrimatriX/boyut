using System.Collections.ObjectModel;
using Boyut.App.Controls;
using Boyut.App.Services;
using Boyut.Core.Analysis;
using Boyut.Core.Model;
using Boyut.Core.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.UI;

namespace Boyut.App.ViewModels;

/// <summary>"What changed between two scans of the same root?" — history picker on top, lazy diff tree below.</summary>
public sealed partial class ChangesViewModel : ObservableObject
{
    private const int MaxChildren = 300;
    private SnapshotDiff? diff;
    private int version;
    private bool suppress;
    private readonly Dictionary<string, FileIndex> loaded = new(StringComparer.OrdinalIgnoreCase);

    public static ChangesViewModel Current { get; } = new();

    private ChangesViewModel()
    {
        Session.RecentChanged += (_, _) => Refresh();
        Columns.SortChanged += (_, _) => Resort();
    }

    public ScanSession Session => ScanSession.Current;

    public GridColumns Columns { get; } = new("changes", "delta", true,
        new GridColumn("name", "Ad") { MinWidth = 200 },
        new GridColumn("old", "Önce", 100) { AlignRight = true, DescendingFirst = true, Priority = 1 },
        new GridColumn("new", "Sonra", 100) { AlignRight = true, DescendingFirst = true, Priority = 2 },
        new GridColumn("delta", "Fark", 110) { AlignRight = true, DescendingFirst = true, Priority = 4 },
        new GridColumn("kind", "Durum", 70) { Priority = 3 });

    public ObservableCollection<string> Roots { get; } = [];
    public ObservableCollection<SnapshotChoice> Snapshots { get; } = [];
    public ObservableCollection<DiffRow> Rows { get; } = [];

    [ObservableProperty] public partial string? SelectedRoot { get; set; }
    [ObservableProperty] public partial SnapshotChoice? Before { get; set; }
    [ObservableProperty] public partial SnapshotChoice? After { get; set; }
    [ObservableProperty] public partial DiffRow? SelectedRow { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial bool HasDiff { get; private set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "";

    [ObservableProperty] public partial string TotalDeltaText { get; private set; } = "";
    [ObservableProperty] public partial Color TotalDeltaColor { get; private set; }
    [ObservableProperty] public partial string FilesDeltaText { get; private set; } = "";
    [ObservableProperty] public partial string FreeDeltaText { get; private set; } = "";
    [ObservableProperty] public partial string SpanText { get; private set; } = "";
    [ObservableProperty] public partial string GrowthText { get; private set; } = "";

    public bool HasSelection => SelectedRow is not null;

    partial void OnSelectedRowChanged(DiffRow? value) => OnPropertyChanged(nameof(HasSelection));

    // ---- choosing what to compare -----------------------------------------------------------

    public async void Refresh()
    {
        var roots = await Task.Run(() => SettingsStore.History.ListRoots());
        string? wanted = Session.PendingChangesRoot ?? SelectedRoot ?? Session.Index?.Metadata.RootPath;
        Session.PendingChangesRoot = null;

        suppress = true;
        Roots.Clear();
        foreach (var (latest, _) in roots) Roots.Add(latest.RootPath);
        suppress = false;

        string? pick = Roots.FirstOrDefault(r => SameRoot(r, wanted)) ?? Roots.FirstOrDefault();
        if (pick is null) { Show("Henüz kaydedilmiş tarama yok. Bir sürücüyü ya da klasörü tarayın; sonraki taramada değişimler burada görünür."); return; }
        if (SelectedRoot == pick) await LoadSnapshotsAsync(pick); // same root: still pick up new scans
        else SelectedRoot = pick;
    }

    partial void OnSelectedRootChanged(string? value)
    {
        if (!suppress && value is not null) _ = LoadSnapshotsAsync(value);
    }

    partial void OnBeforeChanged(SnapshotChoice? value) { if (!suppress) _ = CompareAsync(); }
    partial void OnAfterChanged(SnapshotChoice? value) { if (!suppress) _ = CompareAsync(); }

    private async Task LoadSnapshotsAsync(string root)
    {
        var entries = await Task.Run(() => SettingsStore.History.List(root));
        suppress = true;
        Snapshots.Clear();
        foreach (var e in entries) Snapshots.Add(new SnapshotChoice(e));
        After = Snapshots.ElementAtOrDefault(0);
        Before = Snapshots.ElementAtOrDefault(1);
        suppress = false;
        await CompareAsync();
    }

    private async Task CompareAsync()
    {
        int mine = ++version;
        if (Before is not { } before || After is not { } after)
        {
            Show(Snapshots.Count < 2
                ? "Bu klasörün yalnızca bir taraması var. Bir süre sonra yeniden tarayın (ya da Ayarlar'dan arka plan taramasını açın); farklar burada listelenir."
                : "Karşılaştırmak için iki tarama seçin.");
            return;
        }
        if (before.Entry.File == after.Entry.File) { Show("Aynı tarama iki kez seçildi."); return; }

        // Always older → newer, whichever way round the user picked them.
        if (before.Entry.ScannedAtUtc > after.Entry.ScannedAtUtc) (before, after) = (after, before);

        IsLoading = true;
        try
        {
            var result = await Task.Run(() =>
            {
                var older = Load(before.Entry.File);
                var newer = Load(after.Entry.File);
                lock (loaded)
                {
                    foreach (var stale in loaded.Keys.Except([before.Entry.File, after.Entry.File]).ToList()) loaded.Remove(stale);
                }
                var d = new SnapshotDiff(older, newer);
                return (Diff: d, Growth: d.GrowthPath());
            });
            if (mine != version) return;

            diff = result.Diff;
            var d = result.Diff;
            TotalDeltaText = Ui.SignedSize(d.TotalDelta);
            TotalDeltaColor = Ui.DeltaColor(d.TotalDelta);
            FilesDeltaText = Ui.SignedCount(d.FileCountDelta) + " dosya";
            FreeDeltaText = d.FreeSpaceDelta is { } free ? Ui.SignedSize(free) : "—";
            SpanText = Span(after.Entry.ScannedAtUtc - before.Entry.ScannedAtUtc);
            GrowthText = result.Growth.Count > 1
                ? $"Büyümenin çoğu burada: {d.FullPath(result.Growth[^1])} ({Ui.SignedSize(d.Delta(result.Growth[^1]))})"
                : "";

            Rows.Clear();
            var root = CreateRow(SnapshotDiff.Root, 0);
            Rows.Add(root);
            Expand(root);
            HasDiff = true;
            StatusText = Rows.Count == 1 ? "İki tarama arasında boyut farkı yok." : "";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            if (mine == version) Show("Taramalar karşılaştırılamadı: " + ex.Message);
        }
        finally
        {
            if (mine == version) IsLoading = false;
        }
    }

    private FileIndex Load(string file)
    {
        lock (loaded)
        {
            if (loaded.TryGetValue(file, out var index)) return index;
        }
        var fresh = SnapshotSerializer.Load(file);
        lock (loaded) loaded[file] = fresh;
        return fresh;
    }

    private void Show(string message)
    {
        diff = null;
        Rows.Clear();
        HasDiff = false;
        IsLoading = false;
        StatusText = message;
    }

    private static string Span(TimeSpan span) =>
        span.TotalHours < 1 ? $"{(int)span.TotalMinutes} dakika" :
        span.TotalDays < 1 ? $"{(int)span.TotalHours} saat" :
        $"{(int)span.TotalDays} gün";

    private static bool SameRoot(string a, string? b) =>
        b is not null && string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // ---- rows -------------------------------------------------------------------------------

    private DiffRow CreateRow(DiffEntry e, int depth)
    {
        var d = diff!;
        long delta = d.Delta(e);
        var kind = d.Kind(e);
        bool isDir = d.IsDirectory(e);
        return new DiffRow(this)
        {
            Entry = e,
            Depth = depth,
            Name = d.Name(e),
            OldText = kind == ChangeKind.Added ? "—" : Ui.Size(d.OldSize(e)),
            NewText = kind == ChangeKind.Removed ? "—" : Ui.Size(d.NewSize(e)),
            DeltaText = Ui.SignedSize(delta),
            DeltaColor = Ui.DeltaColor(delta),
            KindText = kind switch { ChangeKind.Added => "Yeni", ChangeKind.Removed => "Silindi", _ => "" },
            Glyph = e.NewNode == FileIndex.RootNode ? Ui.GlyphDrive : isDir ? Ui.GlyphFolder : Ui.GlyphFile,
            GlyphColor = isDir ? Ui.Hex("#E3A82E") : Ui.Hex("#8FA6BA"),
            HasChildren = isDir,
            IsDimmed = kind == ChangeKind.Removed,
        };
    }

    public void Toggle(DiffRow row)
    {
        if (!row.HasChildren) return;
        if (row.IsExpanded) Collapse(row); else Expand(row);
    }

    public void Expand(DiffRow row)
    {
        if (diff is null || row.IsExpanded || !row.HasChildren) return;
        int pos = Rows.IndexOf(row);
        if (pos < 0) return;
        row.IsExpanded = true;
        var kids = Sorted(diff.Children(row.Entry));
        int at = pos + 1;
        foreach (var k in kids.Take(MaxChildren)) Rows.Insert(at++, CreateRow(k, row.Depth + 1));
        if (kids.Count > MaxChildren)
        {
            long rest = kids.Skip(MaxChildren).Sum(diff.Delta);
            Rows.Insert(at, new DiffRow(this)
            {
                Depth = row.Depth + 1,
                Name = $"… {Ui.Count(kids.Count - MaxChildren)} değişiklik daha",
                DeltaText = Ui.SignedSize(rest),
                DeltaColor = Ui.DeltaColor(rest),
                Glyph = Ui.GlyphMore,
                GlyphColor = Ui.Hex("#8A8A8A"),
                IsMore = true,
            });
        }
        if (kids.Count == 0) row.MarkEmpty();
    }

    private List<DiffEntry> Sorted(List<DiffEntry> kids)
    {
        var d = diff!;
        Comparison<DiffEntry>? compare = Columns.SortKey switch
        {
            "name" => (a, b) => string.Compare(d.Name(a), d.Name(b), StringComparison.CurrentCultureIgnoreCase),
            "old" => (a, b) => d.OldSize(a).CompareTo(d.OldSize(b)),
            "new" => (a, b) => d.NewSize(a).CompareTo(d.NewSize(b)),
            "kind" => (a, b) => d.Kind(a).CompareTo(d.Kind(b)),
            _ => null, // "delta": Children() already lists the largest change (either direction) first
        };
        if (compare is null) { if (!Columns.SortDescending) kids.Reverse(); }
        else
        {
            Comparison<DiffEntry> order = Columns.SortDescending ? (a, b) => compare(b, a) : compare;
            kids.Sort(order);
        }
        return kids;
    }

    /// <summary>Rebuilds the rows in the new order, reopening what was open.</summary>
    private void Resort()
    {
        if (diff is null || Rows.Count == 0) return;
        var open = Rows.Where(r => r.IsExpanded && !r.IsMore).Select(r => r.Entry).ToHashSet();
        var selected = SelectedRow is { IsMore: false } s ? s.Entry : (DiffEntry?)null;
        Rows.Clear();
        Rows.Add(CreateRow(SnapshotDiff.Root, 0));
        for (int k = 0; k < Rows.Count; k++) // Expand inserts right after k, so children are visited too
            if (!Rows[k].IsMore && open.Contains(Rows[k].Entry)) Expand(Rows[k]);
        SelectedRow = Rows.FirstOrDefault(r => !r.IsMore && r.Entry == selected);
    }

    public void Collapse(DiffRow row)
    {
        if (!row.IsExpanded) return;
        int pos = Rows.IndexOf(row);
        row.IsExpanded = false;
        if (pos < 0) return;
        while (pos + 1 < Rows.Count && Rows[pos + 1].Depth > row.Depth) Rows.RemoveAt(pos + 1);
    }

    // ---- commands ---------------------------------------------------------------------------

    private string? SelectedPath => SelectedRow is { IsMore: false } row && diff is not null ? diff.FullPath(row.Entry) : null;

    [RelayCommand]
    private void RevealInTree()
    {
        if (SelectedPath is not { } path || After is null || SelectedRow!.Entry.NewNode < 0) return;
        Session.PendingReveal = path;
        if (Session.Index is { } index && SameRoot(index.Metadata.RootPath, SelectedRoot)) App.MainWindow?.Navigate("tree");
        else App.MainWindow?.OpenSnapshot(After.Entry.File);
    }

    [RelayCommand]
    private void RevealInExplorer()
    {
        if (SelectedPath is { } path && (File.Exists(path) || Directory.Exists(path))) FileActions.Reveal(path);
    }

    [RelayCommand]
    private void CopyPath() { if (SelectedPath is { } path) FileActions.CopyText(path); }
}

public sealed record SnapshotChoice(HistoryEntry Entry)
{
    public override string ToString() =>
        $"{Ui.Date(Entry.ScannedAtUtc)} · {Ui.Size(Entry.Header.TotalSize)} · {(Entry.Header.Metadata.Engine == "mft" ? "MFT" : "Klasör")}";
}

public sealed partial class DiffRow(ChangesViewModel owner) : ObservableObject
{
    public DiffEntry Entry { get; init; }
    public int Depth { get; init; }
    public string Name { get; init; } = "";
    public string OldText { get; init; } = "";
    public string NewText { get; init; } = "";
    public string DeltaText { get; init; } = "";
    public Color DeltaColor { get; init; }
    public string KindText { get; init; } = "";
    public string Glyph { get; init; } = Ui.GlyphFile;
    public Color GlyphColor { get; init; }
    public bool HasChildren { get; set; }
    public bool IsMore { get; init; }
    public bool IsDimmed { get; init; }

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public string Chevron => !HasChildren ? "" : IsExpanded ? "" : "";
    public double Opacity => IsDimmed ? 0.6 : 1;

    /// <summary>Expanded but no child changed size (moves inside the folder cancelled out).</summary>
    public void MarkEmpty()
    {
        HasChildren = false;
        OnPropertyChanged(nameof(Chevron));
    }

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Chevron));

    [RelayCommand]
    private void Toggle() => owner.Toggle(this);
}
