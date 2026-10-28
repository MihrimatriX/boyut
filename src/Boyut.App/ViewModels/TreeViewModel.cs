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

/// <summary>
/// The folder tree (TreeSize style: a lazily expanded tree list with share bars) and the details pane beside it.
/// The details pane always describes a folder: the selected one, or the parent of a selected file.
/// </summary>
public sealed partial class TreeViewModel : ObservableObject
{
    private const int PageSize = 400;
    /// <summary>Per folder when whole levels are opened at once, so one huge folder cannot eat the row budget.</summary>
    private const int LevelPageSize = 50;
    private const int MaxLevelRows = 3000;
    private const int MaxTiles = 60;
    private const int MaxInnerTiles = 30;
    private const int MaxContents = 200;
    private const int MaxLargest = 100;
    public const int TabMap = 0, TabContents = 1, TabTypes = 2, TabFiles = 3, TabInfo = 4, TabCount = 5;

    private FileIndex? index;
    /// <summary>Subfolders below each node (recursive), counted once per index.</summary>
    private int[] folderCount = [];
    /// <summary>Children per folder in the header's sort order; empty while sorting largest first (the index's own order).</summary>
    private readonly Dictionary<int, int[]> sortedChildren = [];
    private int baseFolder = -1;
    private (FileIndex? Index, int Folder) typesFor, largestFor;
    private CancellationTokenSource? detailsWork;

    public static TreeViewModel Current { get; } = new();

    private TreeViewModel()
    {
        var s = SettingsStore.Current;
        ShowDetails = s.TreeDetailsVisible;
        DetailsTab = Math.Clamp(s.TreeDetailsTab, 0, TabCount - 1);
        Session.IndexChanged += (_, _) => Load(Session.Index);
        Columns.SortChanged += (_, _) => Load(index); // rebuilds rows, keeps what was open and selected
        Load(Session.Index);
    }

    public ScanSession Session => ScanSession.Current;

    public GridColumns Columns { get; } = new("tree", "size", true,
        new GridColumn("name", "Ad") { MinWidth = 260 },
        new GridColumn("size", "Boyut", 92) { AlignRight = true, DescendingFirst = true, Priority = 6 },
        new GridColumn("allocated", "Diskte", 92) { AlignRight = true, DescendingFirst = true, Priority = 1 },
        new GridColumn("percent", "Pay", 64) { AlignRight = true, DescendingFirst = true, Priority = 5 },
        new GridColumn("files", "Dosya", 80) { AlignRight = true, DescendingFirst = true, Priority = 3 },
        new GridColumn("folders", "Klasör", 72) { AlignRight = true, DescendingFirst = true, Priority = 2 },
        new GridColumn("modified", "Değiştirilme", 96) { DescendingFirst = true, Priority = 4 });

    public ObservableCollection<TreeRow> Rows { get; } = [];
    public ObservableCollection<Crumb> Crumbs { get; } = [];

    // details pane, per tab
    public ObservableCollection<ContentRow> Contents { get; } = [];
    public ObservableCollection<CategoryRow> Categories { get; } = [];
    public ObservableCollection<CategoryRow> Extensions { get; } = [];
    public ObservableCollection<ResultRow> Largest { get; } = [];

    [ObservableProperty] public partial IReadOnlyList<TreemapTile>? Tiles { get; private set; }
    [ObservableProperty] public partial TreeRow? SelectedRow { get; set; }
    [ObservableProperty] public partial bool ShowDetails { get; set; }
    [ObservableProperty] public partial int DetailsTab { get; set; }
    [ObservableProperty] public partial bool IsDetailsBusy { get; private set; }

    // selection
    [ObservableProperty] public partial int SelectedNode { get; private set; } = -1;
    [ObservableProperty] public partial string SelectedName { get; private set; } = "";
    [ObservableProperty] public partial string SelectedPath { get; private set; } = "";
    [ObservableProperty] public partial string SelectedSize { get; private set; } = "";
    [ObservableProperty] public partial string SelectedAllocated { get; private set; } = "";
    [ObservableProperty] public partial string SelectedFiles { get; private set; } = "";
    [ObservableProperty] public partial string SelectedFolders { get; private set; } = "";
    [ObservableProperty] public partial string SelectedKind { get; private set; } = "";
    [ObservableProperty] public partial string SelectedModified { get; private set; } = "";
    [ObservableProperty] public partial string SelectedAttributes { get; private set; } = "";
    [ObservableProperty] public partial string SelectedParentShare { get; private set; } = "";
    [ObservableProperty] public partial string SelectedDiskShare { get; private set; } = "";
    /// <summary>Under the size in the details header: share of the parent, or of the disk for the scan root.</summary>
    [ObservableProperty] public partial string SelectedShareCaption { get; private set; } = "";
    [ObservableProperty] public partial string SelectedGlyph { get; private set; } = Ui.GlyphFolder;
    [ObservableProperty] public partial Color SelectedGlyphColor { get; private set; } = Ui.Hex("#E3A82E");
    [ObservableProperty] public partial bool SelectedIsDirectory { get; private set; }
    [ObservableProperty] public partial bool CanGoUp { get; private set; }

    // the folder the details pane describes
    [ObservableProperty] public partial string BaseCaption { get; private set; } = "";
    [ObservableProperty] public partial string ContentsMore { get; private set; } = "";
    [ObservableProperty] public partial string TypesSummary { get; private set; } = "";

    public bool HasIndex => index is not null;

    /// <summary>Raised when the view should scroll a row into view.</summary>
    public event EventHandler<TreeRow>? RowRevealed;

    partial void OnSelectedRowChanged(TreeRow? value)
    {
        if (value is null || value.IsMore) return;
        Select(value.Node);
    }

    partial void OnShowDetailsChanged(bool value)
    {
        SettingsStore.Current.TreeDetailsVisible = value;
        SettingsStore.Save();
        _ = LoadTabAsync();
    }

    partial void OnDetailsTabChanged(int value)
    {
        SettingsStore.Current.TreeDetailsTab = value;
        SettingsStore.Save();
        _ = LoadTabAsync();
    }

    // ---- loading ----------------------------------------------------------------------------

    private void Load(FileIndex? newIndex)
    {
        // Keep what the user had open (by path) across rescans and deletions.
        var expanded = index is null ? [] : Rows.Where(r => r.IsExpanded).Select(r => index.GetFullPath(r.Node)).ToList();
        string? selected = index is not null && SelectedNode >= 0 && SelectedNode < index.Count ? index.GetFullPath(SelectedNode) : null;

        detailsWork?.Cancel();
        if (!ReferenceEquals(index, newIndex)) folderCount = newIndex is null ? [] : CountFolders(newIndex);
        index = newIndex;
        sortedChildren.Clear();
        baseFolder = -1;
        typesFor = largestFor = default;
        Rows.Clear();
        OnPropertyChanged(nameof(HasIndex));
        if (index is null)
        {
            ClearDetails();
            return;
        }

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

    /// <summary>Nodes are numbered breadth first (<c>Parent(i) &lt; i</c>), so one backward pass totals every subtree.</summary>
    private static int[] CountFolders(FileIndex i)
    {
        var folders = new int[i.Count];
        for (int n = i.Count - 1; n > 0; n--)
            if (i.IsDirectory(n)) folders[i.Parent(n)] += folders[n] + 1;
        return folders;
    }

    private void ClearDetails()
    {
        Tiles = null;
        Contents.Clear();
        Categories.Clear();
        Extensions.Clear();
        Largest.Clear();
        Crumbs.Clear();
        BaseCaption = ContentsMore = TypesSummary = "";
        SelectedNode = -1;
        IsDetailsBusy = false;
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

        string name = node == FileIndex.RootNode ? i.Metadata.RootPath : i.GetName(node);
        if ((flags & NodeFlags.HardLinkSecondary) != 0) name += "  (sabit bağlantı)";
        if ((flags & NodeFlags.AccessDenied) != 0) name += "  (erişim yok)";

        return new TreeRow(this)
        {
            Node = node,
            Depth = depth,
            Name = name,
            SizeText = Ui.Size(i.Size(node)),
            AllocatedText = Ui.Size(i.Allocated(node)),
            Percent = Math.Clamp(share * 100, 0, 100),
            PercentText = node == FileIndex.RootNode ? "" : Ui.Percent(share),
            FilesText = isDir ? Ui.Count(i.FileCount(node)) : "",
            FoldersText = isDir ? Ui.Count(folderCount[node]) : "",
            ModifiedText = Ui.ShortDate(i.ModifiedUtc(node)),
            HasChildren = i.ChildCount(node) > 0,
            Glyph = GlyphOf(node),
            GlyphColor = GlyphColorOf(node),
            BarColor = Ui.LevelColor(depth),
            ShowBar = node != FileIndex.RootNode,
            IsDimmed = (flags & NodeFlags.HardLinkSecondary) != 0,
        };
    }

    private string GlyphOf(int node)
    {
        var i = index!;
        var flags = i.Flags(node);
        return node == FileIndex.RootNode ? Ui.GlyphDrive
            : (flags & NodeFlags.ReparsePoint) != 0 ? Ui.GlyphLink
            : (flags & NodeFlags.CloudPlaceholder) != 0 ? Ui.GlyphCloud
            : i.IsDirectory(node) ? Ui.GlyphFolder : Ui.GlyphFile;
    }

    private Color GlyphColorOf(int node)
    {
        var i = index!;
        if (node == FileIndex.RootNode) return Ui.Hex("#5B9BE6");
        if (!i.IsDirectory(node)) return Ui.CategoryColor(FileCategories.Classify(i, node));
        return (i.Attributes(node) & FileAttributes.System) != 0 ? Ui.Hex("#7F96AA") : Ui.Hex("#E3A82E");
    }

    private TreeRow CreateMoreRow(int parent, int offset, int depth)
    {
        var kids = Kids(parent);
        long rest = 0;
        for (int k = offset; k < kids.Length; k++) rest += index!.EffectiveSize(kids[k]);
        return new TreeRow(this)
        {
            Node = parent,
            Depth = depth,
            IsMore = true,
            MoreOffset = offset,
            Name = $"… {Ui.Count(kids.Length - offset)} öğe daha göster",
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

    public void Expand(TreeRow row) => Expand(row, PageSize);

    private void Expand(TreeRow row, int pageSize)
    {
        if (index is null || row.IsExpanded || !row.HasChildren || row.IsMore) return;
        int pos = Rows.IndexOf(row);
        if (pos < 0) return;
        row.IsExpanded = true;
        InsertChildren(row.Node, 0, row.Depth + 1, pos + 1, pageSize);
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
        InsertChildren(more.Node, more.MoreOffset, more.Depth, pos, PageSize);
    }

    private ReadOnlySpan<int> Kids(int parent)
    {
        var i = index!;
        if (Columns.SortKey is "size" or "percent" && Columns.SortDescending) return i.Children(parent);
        if (sortedChildren.TryGetValue(parent, out var cached)) return cached;

        var kids = i.Children(parent).ToArray();
        var folders = folderCount;
        Comparison<int> compare = Columns.SortKey switch
        {
            "name" => (a, b) => i.NameSpan(a).CompareTo(i.NameSpan(b), StringComparison.CurrentCultureIgnoreCase),
            "allocated" => (a, b) => i.Allocated(a).CompareTo(i.Allocated(b)),
            "files" => (a, b) => i.FileCount(a).CompareTo(i.FileCount(b)),
            "folders" => (a, b) => folders[a].CompareTo(folders[b]),
            "modified" => (a, b) => i.ModifiedTicks(a).CompareTo(i.ModifiedTicks(b)),
            _ => (a, b) => i.Size(a).CompareTo(i.Size(b)),
        };
        Comparison<int> order = Columns.SortDescending ? (a, b) => compare(b, a) : compare;
        Array.Sort(kids, order);
        return sortedChildren[parent] = kids;
    }

    private int InsertChildren(int parent, int offset, int depth, int at, int pageSize)
    {
        var kids = Kids(parent);
        int end = Math.Min(kids.Length, offset + pageSize);
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

    /// <summary>TreeSize's "expand to level": opens every folder down to <paramref name="levels"/>, largest first, within a row budget.</summary>
    [RelayCommand]
    private void ExpandToLevel(string? levels)
    {
        if (index is null || !int.TryParse(levels, out int depth) || depth < 1) return;
        int keep = SelectedNode;
        Rows.Clear();
        Rows.Add(CreateRow(FileIndex.RootNode, 0));
        // Level by level, so a budget cut leaves whole upper levels open rather than one deep branch.
        for (int level = 0; level < depth && Rows.Count < MaxLevelRows; level++)
        {
            var open = Rows.Where(r => r.Depth == level && r.HasChildren && !r.IsMore).ToList();
            foreach (var row in open)
            {
                if (Rows.Count >= MaxLevelRows) break;
                Expand(row, LevelPageSize);
            }
        }
        if (Rows.Count >= MaxLevelRows)
        {
            Session.Notice = $"Ağaç {Ui.Count(MaxLevelRows)} satırla sınırlandı; kalan klasörleri tek tek açabilirsiniz.";
            Session.NoticeIsError = false;
        }
        Reveal(keep >= 0 && keep < index.Count ? keep : FileIndex.RootNode, select: true);
    }

    [RelayCommand]
    private void CollapseAll()
    {
        if (index is null) return;
        Rows.Clear();
        var root = CreateRow(FileIndex.RootNode, 0);
        Rows.Add(root);
        Expand(root);
        Reveal(FileIndex.RootNode, select: true);
    }

    // ---- selection --------------------------------------------------------------------------

    private void Select(int node)
    {
        if (index is null || node < 0 || node >= index.Count) return;
        var i = index;
        SelectedNode = node;
        bool isDir = i.IsDirectory(node);
        SelectedIsDirectory = isDir;
        SelectedName = node == FileIndex.RootNode ? i.Metadata.RootPath : i.GetName(node);
        SelectedPath = i.GetFullPath(node);
        SelectedSize = Ui.Size(i.Size(node));
        SelectedAllocated = Ui.Size(i.Allocated(node));
        SelectedFiles = isDir ? Ui.Count(i.FileCount(node)) : "1";
        SelectedFolders = isDir ? Ui.Count(folderCount[node]) : "—";
        SelectedKind = isDir ? (node == FileIndex.RootNode ? "Tarama kökü" : "Klasör") : FileCategories.DisplayName(FileCategories.Classify(i, node));
        SelectedModified = Ui.Date(i.ModifiedUtc(node));
        string attrs = CsvExporter.DescribeAttributes(i, node);
        SelectedAttributes = attrs.Length == 0 ? "—" : attrs;
        SelectedGlyph = GlyphOf(node);
        SelectedGlyphColor = GlyphColorOf(node);
        int parent = i.Parent(node);
        SelectedParentShare = parent >= 0 && i.Size(parent) > 0 ? Ui.Percent((double)i.EffectiveSize(node) / i.Size(parent)) : "—";
        long? volume = i.Metadata.VolumeTotalBytes;
        SelectedDiskShare = Ui.Percent((double)i.Size(node) / (volume is > 0 ? volume.Value : Math.Max(1, i.TotalSize)));
        SelectedShareCaption = parent >= 0 ? "üst klasördeki payı " + SelectedParentShare
            : volume is > 0 ? "diskteki payı " + SelectedDiskShare : "";
        CanGoUp = node != FileIndex.RootNode;

        UpdateCrumbs(node);
        int folder = isDir ? node : Math.Max(FileIndex.RootNode, parent);
        if (folder != baseFolder)
        {
            baseFolder = folder;
            UpdateBase();
        }
    }

    private void UpdateCrumbs(int node)
    {
        Crumbs.Clear();
        foreach (int n in index!.GetAncestry(node))
            Crumbs.Add(new Crumb(n, n == FileIndex.RootNode ? index.Metadata.RootPath.TrimEnd('\\', '/') : index.GetName(n)));
    }

    // ---- details pane -----------------------------------------------------------------------

    /// <summary>Map and contents are cheap (direct children); types and largest files walk the subtree, so they load on demand.</summary>
    private void UpdateBase()
    {
        var i = index!;
        int folder = baseFolder;
        var kids = i.Children(folder);
        string name = folder == FileIndex.RootNode ? i.Metadata.RootPath : i.GetName(folder);
        BaseCaption = $"{name} · {Ui.Count(kids.Length)} öğe · {Ui.Size(i.Size(folder))}";

        Tiles = BuildTiles(folder, MaxTiles, parentColor: null);

        Contents.Clear();
        long total = Math.Max(1, i.Size(folder));
        for (int k = 0; k < kids.Length && k < MaxContents; k++)
        {
            int c = kids[k];
            long s = i.EffectiveSize(c);
            bool dir = i.IsDirectory(c);
            Contents.Add(new ContentRow(c, i.GetName(c), Ui.Size(s), s * 100.0 / total, Ui.Percent((double)s / total),
                dir ? Ui.Hex("#3B82F6") : Ui.CategoryColor(FileCategories.Classify(i, c)), GlyphOf(c), GlyphColorOf(c),
                dir ? $"{Ui.Count(i.FileCount(c))} dosya" : FileCategories.DisplayName(FileCategories.Classify(i, c))));
        }
        ContentsMore = kids.Length > MaxContents ? $"… ve {Ui.Count(kids.Length - MaxContents)} öğe daha (en büyükleri listelendi)" : "";

        _ = LoadTabAsync();
    }

    /// <param name="parentColor">Set for the second level: folders get a tint of their parent's colour.</param>
    private List<TreemapTile> BuildTiles(int folder, int max, Color? parentColor)
    {
        var i = index!;
        var kids = i.Children(folder); // largest first
        double folderSize = Math.Max(1, i.Size(folder));
        var tiles = new List<TreemapTile>(Math.Min(kids.Length, max) + 1);
        long rest = 0;
        int restCount = 0;
        foreach (int c in kids)
        {
            long s = i.EffectiveSize(c);
            if (s <= 0) continue;
            if (tiles.Count >= max) { rest += s; restCount++; continue; }
            bool dir = i.IsDirectory(c);
            var category = dir ? FileCategory.Folder : FileCategories.Classify(i, c);
            Color color = !dir ? Ui.CategoryColor(category)
                : parentColor is { } p ? Ui.Tint(p, 0.35)
                : Ui.TilePalette[tiles.Count % Ui.TilePalette.Length];
            string detail = $"{Ui.Percent(s / folderSize)} · " + (dir ? $"{Ui.Count(i.FileCount(c))} dosya" : FileCategories.DisplayName(category));
            tiles.Add(new TreemapTile(c, i.GetName(c), Ui.Size(s), s, color, dir)
            {
                Detail = detail,
                Children = parentColor is null && dir && i.ChildCount(c) > 0 ? BuildTiles(c, MaxInnerTiles, color) : null,
            });
        }
        if (rest > 0)
            tiles.Add(new TreemapTile(-1, $"{Ui.Count(restCount)} öğe daha", Ui.Size(rest), rest, Ui.Hex("#C9CED3"), false)
            {
                Detail = Ui.Percent(rest / folderSize),
            });
        return tiles;
    }

    private async Task LoadTabAsync()
    {
        if (index is not { } i || baseFolder < 0 || !ShowDetails) return;
        int folder = baseFolder, tab = DetailsTab;
        if (tab == TabTypes && typesFor == (i, folder)) return;
        if (tab == TabFiles && largestFor == (i, folder)) return;
        if (tab is not (TabTypes or TabFiles)) { detailsWork?.Cancel(); IsDetailsBusy = false; return; }

        detailsWork?.Cancel();
        var cts = detailsWork = new CancellationTokenSource();
        IsDetailsBusy = true;
        try
        {
            await Task.Delay(150, cts.Token); // arrowing through the tree should not start a subtree walk per row
            if (tab == TabTypes)
            {
                var types = await Task.Run(() => TypeStatistics.Compute(i, folder, topExtensions: 40), cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(i, index) || folder != baseFolder) return;
                long total = Math.Max(1, types.TotalBytes);
                Categories.Clear();
                foreach (var c in types.Categories)
                    Categories.Add(new CategoryRow(c.Name, Ui.Size(c.Bytes), c.Bytes * 100.0 / total, Ui.Percent((double)c.Bytes / total),
                        Ui.Count(c.Files) + " dosya", Ui.CategoryColor(c.Category)));
                Extensions.Clear();
                foreach (var e in types.Extensions)
                    Extensions.Add(new CategoryRow(e.Extension.StartsWith('(') ? e.Extension : "." + e.Extension, Ui.Size(e.Bytes),
                        e.Bytes * 100.0 / total, Ui.Percent((double)e.Bytes / total), Ui.Count(e.Files) + " dosya", Ui.CategoryColor(e.Category)));
                TypesSummary = $"{Ui.Count(types.TotalFiles)} dosya · {Ui.Size(types.TotalBytes)}";
                typesFor = (i, folder);
            }
            else
            {
                var largest = await Task.Run(() => TypeStatistics.LargestFiles(i, folder, MaxLargest), cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(i, index) || folder != baseFolder) return;
                Largest.Clear();
                foreach (int n in largest) Largest.Add(ResultRow.Create(i, n));
                largestFor = (i, folder);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(detailsWork, cts)) IsDetailsBusy = false;
        }
    }

    // ---- commands ---------------------------------------------------------------------------

    public void OpenTile(int node)
    {
        if (index is null || node < 0 || node >= index.Count) return;
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

    public void ShowInTree(ResultRow row)
    {
        if (index is null) return;
        int node = index.FindByPath(row.FullPath);
        if (node >= 0) Reveal(node, select: true);
    }
}

public sealed partial class TreeRow(TreeViewModel owner) : ObservableObject
{
    public int Node { get; init; }
    public int Depth { get; init; }
    public string Name { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string AllocatedText { get; init; } = "";
    public double Percent { get; init; }
    public string PercentText { get; init; } = "";
    public string FilesText { get; init; } = "";
    public string FoldersText { get; init; } = "";
    public string ModifiedText { get; init; } = "";
    public bool HasChildren { get; init; }
    public bool IsMore { get; init; }
    public int MoreOffset { get; init; }
    public bool IsDimmed { get; init; }
    public string Glyph { get; init; } = Ui.GlyphFile;
    public Color GlyphColor { get; init; }
    public Color BarColor { get; init; }
    public bool ShowBar { get; init; }

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public string Chevron => !HasChildren || IsMore ? "" : IsExpanded ? "" : "";
    public double Opacity => IsDimmed ? 0.6 : 1;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Chevron));

    [RelayCommand]
    private void Toggle() => owner.Toggle(this);
}

public sealed record Crumb(int Node, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One child in the details pane's "İçerik" bar list.</summary>
public sealed record ContentRow(int Node, string Name, string SizeText, double Percent, string PercentText, Color Color,
    string Glyph, Color GlyphColor, string Detail);
