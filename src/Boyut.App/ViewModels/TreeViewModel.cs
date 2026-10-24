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

public sealed partial class TreeViewModel : ObservableObject
{
    private const int PageSize = 400;
    private const int MaxTiles = 60;
    private FileIndex? index;
    /// <summary>Children per folder in the header's sort order; empty while sorting largest first (the index's own order).</summary>
    private readonly Dictionary<int, int[]> sortedChildren = [];

    public static TreeViewModel Current { get; } = new();

    private TreeViewModel()
    {
        Session.IndexChanged += (_, _) => Load(Session.Index);
        Columns.SortChanged += (_, _) => Load(index); // rebuilds rows, keeps what was open and selected
        Load(Session.Index);
    }

    public ScanSession Session => ScanSession.Current;

    public GridColumns Columns { get; } = new("tree", "size", true,
        new GridColumn("name", "Ad") { MinWidth = 200 },
        new GridColumn("size", "Boyut", 90) { AlignRight = true, DescendingFirst = true, Priority = 4 },
        new GridColumn("share", "Üst klasördeki payı", 170) { Sortable = false, Priority = 2 },
        new GridColumn("files", "Dosya", 80) { AlignRight = true, DescendingFirst = true, Priority = 1 },
        new GridColumn("modified", "Değiştirilme", 90) { DescendingFirst = true, Priority = 3 });

    public ObservableCollection<TreeRow> Rows { get; } = [];
    public ObservableCollection<Crumb> Crumbs { get; } = [];
    public ObservableCollection<BarItem> Bars { get; } = [];

    [ObservableProperty] public partial IReadOnlyList<TreemapTile>? Tiles { get; private set; }
    [ObservableProperty] public partial TreeRow? SelectedRow { get; set; }
    [ObservableProperty] public partial bool ShowTreemap { get; set; } = true;

    // summary
    [ObservableProperty] public partial string TotalText { get; private set; } = "";
    [ObservableProperty] public partial string AllocatedText { get; private set; } = "";
    [ObservableProperty] public partial string FilesText { get; private set; } = "";
    [ObservableProperty] public partial string EngineText { get; private set; } = "";
    [ObservableProperty] public partial string FreeText { get; private set; } = "";
    [ObservableProperty] public partial string CapacityText { get; private set; } = "";
    [ObservableProperty] public partial double UsedPercent { get; private set; }
    [ObservableProperty] public partial bool HasVolumeInfo { get; private set; }

    // selection details
    [ObservableProperty] public partial int SelectedNode { get; private set; } = -1;
    [ObservableProperty] public partial string SelectedName { get; private set; } = "";
    [ObservableProperty] public partial string SelectedPath { get; private set; } = "";
    [ObservableProperty] public partial string SelectedSize { get; private set; } = "";
    [ObservableProperty] public partial string SelectedAllocated { get; private set; } = "";
    [ObservableProperty] public partial string SelectedFiles { get; private set; } = "";
    [ObservableProperty] public partial string SelectedKind { get; private set; } = "";
    [ObservableProperty] public partial string SelectedModified { get; private set; } = "";
    [ObservableProperty] public partial string SelectedAttributes { get; private set; } = "";
    [ObservableProperty] public partial string SelectedShare { get; private set; } = "";
    [ObservableProperty] public partial string SelectedGlyph { get; private set; } = Ui.GlyphFolder;
    [ObservableProperty] public partial bool SelectedIsDirectory { get; private set; }
    [ObservableProperty] public partial bool CanGoUp { get; private set; }

    // treemap header
    [ObservableProperty] public partial string BaseName { get; private set; } = "";
    [ObservableProperty] public partial string BaseSummary { get; private set; } = "";

    public bool HasIndex => index is not null;
    public bool ShowBars => !ShowTreemap;

    /// <summary>Raised when the view should scroll a row into view.</summary>
    public event EventHandler<TreeRow>? RowRevealed;

    partial void OnShowTreemapChanged(bool value) => OnPropertyChanged(nameof(ShowBars));

    partial void OnSelectedRowChanged(TreeRow? value)
    {
        if (value is null || value.IsMore) return;
        Select(value.Node);
    }

    // ---- loading ----------------------------------------------------------------------------

    private void Load(FileIndex? newIndex)
    {
        // Keep what the user had open (by path) across rescans and deletions.
        var expanded = index is null ? [] : Rows.Where(r => r.IsExpanded).Select(r => index.GetFullPath(r.Node)).ToList();
        string? selected = index is not null && SelectedNode >= 0 && SelectedNode < index.Count ? index.GetFullPath(SelectedNode) : null;

        index = newIndex;
        sortedChildren.Clear();
        Rows.Clear();
        OnPropertyChanged(nameof(HasIndex));
        if (index is null) return;

        UpdateSummary();
        var root = CreateRow(FileIndex.RootNode, 0);
        Rows.Add(root);
        Expand(root);

        foreach (string path in expanded.OrderBy(p => p.Length))
        {
            int node = index.FindByPath(path);
            if (node > 0) Reveal(node, select: false, expand: true);
        }

        int target = selected is null ? -1 : index.FindByPath(selected);
        if (Session.PendingReveal is { } pending)
        {
            target = index.FindByPath(pending);
            Session.PendingReveal = null;
        }
        Reveal(target >= 0 ? target : FileIndex.RootNode, select: true, expand: target <= 0);
    }

    /// <summary>Called when the page is shown again (e.g. "İncele" from another page).</summary>
    public void ApplyPendingReveal()
    {
        if (index is null || Session.PendingReveal is not { } path) return;
        Session.PendingReveal = null;
        int node = index.FindByPath(path);
        if (node >= 0) Reveal(node, select: true, expand: true);
    }

    private void UpdateSummary()
    {
        if (index is null) return;
        var m = index.Metadata;
        TotalText = Ui.Size(index.TotalSize);
        AllocatedText = "Diskte " + Ui.Size(index.TotalAllocated);
        FilesText = Ui.Count(index.TotalFiles);
        EngineText = (m.Engine == "mft" ? "MFT" : "Klasör taraması") + $" · {Core.Formatting.SizeFormatter.FormatDuration(m.Duration)} · {Ui.Relative(m.ScannedAtUtc)}";
        HasVolumeInfo = m.VolumeTotalBytes is > 0 && m.VolumeFreeBytes is not null;
        if (HasVolumeInfo)
        {
            long total = m.VolumeTotalBytes!.Value, free = m.VolumeFreeBytes!.Value;
            FreeText = Ui.Size(free);
            CapacityText = $"{Ui.Size(total)} kapasite";
            UsedPercent = (total - free) * 100.0 / total;
        }
    }

    // ---- rows -------------------------------------------------------------------------------

    private TreeRow CreateRow(int node, int depth)
    {
        var i = index!;
        bool isDir = i.IsDirectory(node);
        int parent = i.Parent(node);
        long parentSize = parent >= 0 ? i.Size(parent) : i.Size(node);
        double share = parentSize > 0 ? (double)i.EffectiveSize(node) / parentSize : 1;
        var flags = i.Flags(node);
        bool system = (i.Attributes(node) & FileAttributes.System) != 0;

        string glyph = node == FileIndex.RootNode ? Ui.GlyphDrive
            : (flags & NodeFlags.ReparsePoint) != 0 ? Ui.GlyphLink
            : (flags & NodeFlags.CloudPlaceholder) != 0 ? Ui.GlyphCloud
            : isDir ? Ui.GlyphFolder : Ui.GlyphFile;
        Color glyphColor = node == FileIndex.RootNode ? Ui.Hex("#5B9BE6")
            : isDir ? (system ? Ui.Hex("#7F96AA") : Ui.Hex("#E3A82E"))
            : Ui.CategoryColor(FileCategories.Classify(i, node));

        string name = node == FileIndex.RootNode ? i.Metadata.RootPath : i.GetName(node);
        if ((flags & NodeFlags.HardLinkSecondary) != 0) name += "  (sabit bağlantı)";
        if ((flags & NodeFlags.AccessDenied) != 0) name += "  (erişim yok)";

        return new TreeRow(this)
        {
            Node = node,
            Depth = depth,
            Name = name,
            SizeText = Ui.Size(i.Size(node)),
            Percent = share * 100,
            PercentText = Ui.Percent(share),
            FilesText = isDir ? Ui.Count(i.FileCount(node)) : "",
            ModifiedText = Ui.ShortDate(i.ModifiedUtc(node)),
            HasChildren = i.ChildCount(node) > 0,
            Glyph = glyph,
            GlyphColor = glyphColor,
            IsDimmed = (flags & NodeFlags.HardLinkSecondary) != 0,
        };
    }

    private TreeRow CreateMoreRow(int parent, int offset, int depth)
    {
        int remaining = index!.ChildCount(parent) - offset;
        long rest = 0;
        var kids = Kids(parent);
        for (int k = offset; k < kids.Length; k++) rest += index.EffectiveSize(kids[k]);
        return new TreeRow(this)
        {
            Node = parent,
            Depth = depth,
            IsMore = true,
            MoreOffset = offset,
            Name = $"… {Ui.Count(remaining)} öğe daha göster",
            SizeText = Ui.Size(rest),
            Glyph = Ui.GlyphMore,
            GlyphColor = Ui.Hex("#8A8A8A"),
        };
    }

    public void Toggle(TreeRow row)
    {
        if (index is null) return;
        if (row.IsMore) { LoadMore(row); return; }
        if (!row.HasChildren) return;
        if (row.IsExpanded) Collapse(row); else Expand(row);
    }

    public void Expand(TreeRow row)
    {
        if (index is null || row.IsExpanded || !row.HasChildren) return;
        int pos = Rows.IndexOf(row);
        if (pos < 0) return;
        row.IsExpanded = true;
        InsertChildren(row.Node, 0, row.Depth + 1, pos + 1);
    }

    public void Collapse(TreeRow row)
    {
        if (!row.IsExpanded) return;
        int pos = Rows.IndexOf(row);
        row.IsExpanded = false;
        if (pos < 0) return;
        while (pos + 1 < Rows.Count && Rows[pos + 1].Depth > row.Depth) Rows.RemoveAt(pos + 1);
    }

    private void LoadMore(TreeRow more)
    {
        int pos = Rows.IndexOf(more);
        if (pos < 0) return;
        Rows.RemoveAt(pos);
        InsertChildren(more.Node, more.MoreOffset, more.Depth, pos);
    }

    private ReadOnlySpan<int> Kids(int parent)
    {
        var i = index!;
        if (Columns.SortKey == "size" && Columns.SortDescending) return i.Children(parent);
        if (sortedChildren.TryGetValue(parent, out var cached)) return cached;

        var kids = i.Children(parent).ToArray();
        Comparison<int> compare = Columns.SortKey switch
        {
            "name" => (a, b) => i.NameSpan(a).CompareTo(i.NameSpan(b), StringComparison.CurrentCultureIgnoreCase),
            "files" => (a, b) => i.FileCount(a).CompareTo(i.FileCount(b)),
            "modified" => (a, b) => i.ModifiedTicks(a).CompareTo(i.ModifiedTicks(b)),
            _ => (a, b) => i.Size(a).CompareTo(i.Size(b)),
        };
        Comparison<int> order = Columns.SortDescending ? (a, b) => compare(b, a) : compare;
        Array.Sort(kids, order);
        return sortedChildren[parent] = kids;
    }

    private int InsertChildren(int parent, int offset, int depth, int at)
    {
        var kids = Kids(parent);
        int end = Math.Min(kids.Length, offset + PageSize);
        for (int k = offset; k < end; k++) Rows.Insert(at++, CreateRow(kids[k], depth));
        if (end < kids.Length) Rows.Insert(at++, CreateMoreRow(parent, end, depth));
        return at;
    }

    private TreeRow? FindRow(int node)
    {
        foreach (var r in Rows)
            if (r.Node == node && !r.IsMore) return r;
        return null;
    }

    /// <summary>Expands every ancestor, loading more pages if needed, then optionally selects the node.</summary>
    public void Reveal(int node, bool select = true, bool expand = false)
    {
        if (index is null || node < 0 || node >= index.Count) return;
        var chain = index.GetAncestry(node);
        TreeRow? row = null;
        for (int k = 0; k < chain.Length; k++)
        {
            row = FindRow(chain[k]);
            while (row is null && k > 0)
            {
                // The node is beyond a "… more" row of its parent: open pages until it appears.
                var more = Rows.FirstOrDefault(r => r.IsMore && r.Node == chain[k - 1]);
                if (more is null) break;
                LoadMore(more);
                row = FindRow(chain[k]);
            }
            if (row is null) return;
            if (k < chain.Length - 1) Expand(row);
        }
        if (row is null) return;
        if (expand) Expand(row);
        if (select)
        {
            if (ReferenceEquals(SelectedRow, row)) Select(node);
            else SelectedRow = row; // OnSelectedRowChanged → Select
            RowRevealed?.Invoke(this, row);
        }
    }

    // ---- selection --------------------------------------------------------------------------

    private void Select(int node)
    {
        if (index is null || node < 0 || node >= index.Count) return;
        SelectedNode = node;
        var i = index;
        bool isDir = i.IsDirectory(node);
        SelectedIsDirectory = isDir;
        SelectedName = node == FileIndex.RootNode ? i.Metadata.RootPath : i.GetName(node);
        SelectedPath = i.GetFullPath(node);
        SelectedSize = Ui.Size(i.Size(node));
        SelectedAllocated = Ui.Size(i.Allocated(node));
        SelectedFiles = isDir ? Ui.Count(i.FileCount(node)) : "1";
        SelectedKind = isDir ? (node == FileIndex.RootNode ? "Tarama kökü" : "Klasör") : FileCategories.DisplayName(FileCategories.Classify(i, node));
        SelectedModified = Ui.Date(i.ModifiedUtc(node));
        string attrs = CsvExporter.DescribeAttributes(i, node);
        SelectedAttributes = attrs.Length == 0 ? "—" : attrs;
        SelectedGlyph = isDir ? Ui.GlyphFolder : Ui.GlyphFile;
        long? volume = i.Metadata.VolumeTotalBytes;
        SelectedShare = volume is > 0 ? Ui.Percent((double)i.Size(node) / volume.Value) : Ui.Percent((double)i.Size(node) / Math.Max(1, i.TotalSize));
        CanGoUp = node != FileIndex.RootNode;

        UpdateCrumbs(node);
        UpdateBase(isDir ? node : i.Parent(node), node);
    }

    private void UpdateCrumbs(int node)
    {
        Crumbs.Clear();
        foreach (int n in index!.GetAncestry(node))
            Crumbs.Add(new Crumb(n, n == FileIndex.RootNode ? index.Metadata.RootPath.TrimEnd('\\', '/') : index.GetName(n)));
    }

    private void UpdateBase(int folder, int selected)
    {
        var i = index!;
        if (folder < 0) folder = FileIndex.RootNode;
        var kids = i.Children(folder);
        BaseName = folder == FileIndex.RootNode ? i.Metadata.RootPath : i.GetName(folder);
        BaseSummary = $"{Ui.Count(kids.Length)} öğe · {Ui.Size(i.Size(folder))}";

        var tiles = new List<TreemapTile>(Math.Min(kids.Length, MaxTiles) + 1);
        long rest = 0;
        int restCount = 0;
        for (int k = 0; k < kids.Length; k++)
        {
            int c = kids[k];
            long s = i.EffectiveSize(c);
            if (k >= MaxTiles) { rest += s; restCount++; continue; }
            if (s <= 0) continue;
            var color = i.IsDirectory(c) ? Ui.TilePalette[tiles.Count % Ui.TilePalette.Length] : Ui.CategoryColor(FileCategories.Classify(i, c));
            tiles.Add(new TreemapTile(c, i.GetName(c), Ui.Size(s), s, color, c == selected, i.IsDirectory(c)));
        }
        if (rest > 0) tiles.Add(new TreemapTile(-1, $"{Ui.Count(restCount)} öğe daha", Ui.Size(rest), rest, Ui.Hex("#D5D9DD"), false, false));
        Tiles = tiles;

        Bars.Clear();
        long total = Math.Max(1, i.Size(folder));
        foreach (var t in tiles)
            Bars.Add(new BarItem(t.Node, t.Name, t.SizeText, t.Value * 100 / total, Ui.Percent(t.Value / total), t.Color));
    }

    // ---- commands ---------------------------------------------------------------------------

    public void OpenTile(int node)
    {
        if (index is null || node < 0) return;
        Reveal(node, select: true, expand: index.IsDirectory(node));
    }

    public void OpenCrumb(Crumb crumb) => Reveal(crumb.Node, select: true, expand: true);

    [RelayCommand]
    private void GoUp()
    {
        if (index is null || SelectedNode <= 0) return;
        Reveal(index.Parent(SelectedNode), select: true);
    }

    [RelayCommand]
    private void RevealInExplorer() { if (SelectedPath.Length > 0) FileActions.Reveal(SelectedPath); }

    [RelayCommand]
    private void OpenItem() { if (SelectedPath.Length > 0) FileActions.Open(SelectedPath); }

    [RelayCommand]
    private void CopyPath() { if (SelectedPath.Length > 0) FileActions.CopyText(SelectedPath); }

    [RelayCommand]
    private void OpenTerminal()
    {
        if (index is null || SelectedNode < 0) return;
        int folder = index.IsDirectory(SelectedNode) ? SelectedNode : index.Parent(SelectedNode);
        FileActions.OpenTerminal(index.GetFullPath(folder));
    }

    [RelayCommand]
    private void SearchHere()
    {
        if (index is null || SelectedNode < 0) return;
        int folder = index.IsDirectory(SelectedNode) ? SelectedNode : index.Parent(SelectedNode);
        Session.PendingSearch = (index.GetFullPath(folder), null);
        App.MainWindow?.Navigate("search");
    }

    [RelayCommand]
    private void Refresh()
    {
        if (index is not null && !Session.IsScanning) App.MainWindow?.Refresh();
    }

    [RelayCommand]
    private void Rescan()
    {
        if (index is not null && !Session.IsScanning) App.MainWindow?.StartScan(index.Metadata.RootPath);
    }

    [RelayCommand]
    private void RescanSelected()
    {
        if (index is null || SelectedNode < 0 || Session.IsScanning) return;
        int folder = index.IsDirectory(SelectedNode) ? SelectedNode : index.Parent(SelectedNode);
        App.MainWindow?.StartScan(index.GetFullPath(folder));
    }

    [RelayCommand]
    private Task ExportAll() => FileActions.ExportCsvAsync(FileIndex.RootNode, wholeTree: true);

    [RelayCommand]
    private Task ExportSelected() => FileActions.ExportCsvAsync(Math.Max(0, SelectedNode), wholeTree: true);

    [RelayCommand]
    private Task ExportLevel() => FileActions.ExportCsvAsync(Math.Max(0, SelectedNode), wholeTree: false);

    [RelayCommand]
    private Task SaveSnapshot() => FileActions.SaveSnapshotAsync();

    [RelayCommand]
    private void ToggleView() => ShowTreemap = !ShowTreemap;
}

public sealed partial class TreeRow(TreeViewModel owner) : ObservableObject
{
    public int Node { get; init; }
    public int Depth { get; init; }
    public string Name { get; init; } = "";
    public string SizeText { get; init; } = "";
    public double Percent { get; init; }
    public string PercentText { get; init; } = "";
    public string FilesText { get; init; } = "";
    public string ModifiedText { get; init; } = "";
    public bool HasChildren { get; init; }
    public bool IsMore { get; init; }
    public int MoreOffset { get; init; }
    public bool IsDimmed { get; init; }
    public string Glyph { get; init; } = Ui.GlyphFile;
    public Color GlyphColor { get; init; }

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public string Chevron => !HasChildren || IsMore ? "" : IsExpanded ? "" : "";
    public double Opacity => IsDimmed ? 0.6 : 1;
    public bool ShowBar => !IsMore;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Chevron));

    [RelayCommand]
    private void Toggle() => owner.Toggle(this);
}

public sealed record Crumb(int Node, string Name)
{
    public override string ToString() => Name;
}

public sealed record BarItem(int Node, string Name, string SizeText, double Percent, string PercentText, Color Color);
