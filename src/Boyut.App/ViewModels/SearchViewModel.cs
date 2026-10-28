using System.Collections.ObjectModel;
using Boyut.App.Controls;
using Boyut.App.Services;
using Boyut.Core.Analysis;
using Boyut.Core.Model;
using Boyut.Core.Persistence;
using Boyut.Core.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.UI;

namespace Boyut.App.ViewModels;

public sealed partial class SearchViewModel : ObservableObject
{
    private const int MaxRows = 5000;
    private CancellationTokenSource? pending;
    private SearchResult lastResult = SearchResult.Empty;
    private int scopeNode = FileIndex.RootNode;
    /// <summary>A query ran and its results are listed (an empty pattern runs nothing: no "no matches" then).</summary>
    private bool searched;

    public static SearchViewModel Current { get; } = new();

    private SearchViewModel()
    {
        foreach (var s in SettingsStore.Current.SavedSearches) SavedSearches.Add(new SavedSearchItem(s));
        Columns.SortChanged += (_, _) => Fill();
        Session.IndexChanged += (_, _) =>
        {
            // Rows hold node numbers of the old index; never let them be acted on.
            Results.Clear();
            SelectedCount = 0;
            SelectionText = "";
            ResolveScope();
            Schedule(immediate: true);
        };
    }

    public ScanSession Session => ScanSession.Current;

    public ObservableCollection<ResultRow> Results { get; } = [];
    public ObservableCollection<TokenChip> Tokens { get; } = [];
    public ObservableCollection<SavedSearchItem> SavedSearches { get; } = [];

    public IReadOnlyList<SyntaxHint> Syntax { get; } =
    [
        new("*.mp4;*.mkv", "Noktalı virgül = veya. Joker * ve ? ad içinde çalışır."),
        new(@"**\node_modules", "** her derinlikte; ayraç içeren desenler tam yolda eşleşir."),
        new("IMG_[0-9]???.*", "[abc], [0-9], [!x] karakter sınıfları."),
        new("size:>1gb", "Boyut: >, <, >=, aralık 10mb..500mb, boş: size:empty"),
        new("ext:psd,tif", "Uzantılardan biri (virgülle)."),
        new("type:video,görsel", "Tür: video, ses, görsel, belge, arşiv, kod, uygulama, sistem"),
        new("modified:2025", "Tarih: 2025, 2025-03, 14.03.2025, bugün, bu hafta, <2024, 2024..2025"),
        new("age:>90d", "Yaş: >90d daha eski, <1w daha yeni (d, w, m, y)."),
        new("attr:hidden", "Öznitelik: hidden, system, readonly, compressed, link, cloud, hardlink"),
        new("in:Downloads", "Yolunda bu geçenler; joker de olur: in:\"*\\Steam\\*\""),
        new("kind:dir", "Yalnızca klasörler (kind:file yalnızca dosyalar)."),
        new("-*.log", "Başına - koyunca hariç tutar: -in:Windows, -attr:system"),
    ];

    public IReadOnlyList<string> SizeFilters { get; } = ["Boyut: tümü", "> 100 MB", "> 1 GB", "> 10 GB", "< 1 MB", "Boş"];
    public IReadOnlyList<string> DateFilters { get; } = ["Tarih: tümü", "Son 7 gün", "Son 30 gün", "Son 1 yıl", "1 yıldan eski", "3 yıldan eski"];
    public IReadOnlyList<string> TypeFilters { get; } =
        ["Tür: tümü", "Video", "Ses", "Görsel", "Belge", "Arşiv", "Disk imajı", "Kod", "Uygulama", "Oyun ve 3D", "Sistem ve log", "Klasör"];

    [ObservableProperty] public partial string QueryText { get; set; } = "";
    [ObservableProperty] public partial int ModeIndex { get; set; }
    [ObservableProperty] public partial bool CaseSensitive { get; set; }
    [ObservableProperty] public partial bool FullPath { get; set; }
    [ObservableProperty] public partial bool IncludeDirectories { get; set; } = true;
    [ObservableProperty] public partial int SizeFilter { get; set; }
    [ObservableProperty] public partial int DateFilter { get; set; }
    [ObservableProperty] public partial int TypeFilter { get; set; }

    [ObservableProperty] public partial string? Error { get; private set; }
    [ObservableProperty] public partial string Summary { get; private set; } = "Bir desen yazın.";
    [ObservableProperty] public partial string Detail { get; private set; } = "";
    [ObservableProperty] public partial bool IsSearching { get; private set; }
    [ObservableProperty] public partial string? ScopePath { get; private set; }
    [ObservableProperty] public partial string SelectionText { get; private set; } = "";
    [ObservableProperty] public partial int SelectedCount { get; private set; }

    public GridColumns Columns { get; } = new("search", "size", true,
        new GridColumn("name", "Ad") { MinWidth = 160 },
        new GridColumn("folder", "Konum", 320) { MinWidth = 100, Priority = 3 },
        new GridColumn("size", "Boyut", 90) { AlignRight = true, DescendingFirst = true, Priority = 4 },
        new GridColumn("modified", "Değiştirilme", 96) { DescendingFirst = true, Priority = 2 },
        new GridColumn("attributes", "Öznitelik", 120) { Sortable = false, Priority = 1 });

    public bool HasScope => ScopePath is not null;
    public string ScopeText => ScopePath is null ? "Tüm tarama" : ScopePath;
    public bool HasSelection => SelectedCount > 0;
    public bool HasResults => Results.Count > 0;
    public bool ShowEmpty => searched && !IsSearching && Results.Count == 0 && Error is null && Session.HasIndex;
    public string PlaceholderText => ModeIndex switch
    {
        1 => @"^IMG_\d{4}\.(jpg|heic)$   size:>1mb",
        2 => "fragman 4k   in:Videos",
        _ => @"*.mp4;*.mkv   **\node_modules   size:>1gb",
    };

    partial void OnQueryTextChanged(string value) => Schedule();
    partial void OnModeIndexChanged(int value) { OnPropertyChanged(nameof(PlaceholderText)); Schedule(); }
    partial void OnCaseSensitiveChanged(bool value) => Schedule();
    partial void OnFullPathChanged(bool value) => Schedule();
    partial void OnIncludeDirectoriesChanged(bool value) => Schedule();
    partial void OnSizeFilterChanged(int value) => Schedule();
    partial void OnDateFilterChanged(int value) => Schedule();
    partial void OnTypeFilterChanged(int value) => Schedule();
    partial void OnScopePathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasScope));
        OnPropertyChanged(nameof(ScopeText));
    }
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(ShowEmpty));
    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(HasSelection));

    /// <summary>Applies a hand-off from another page (global search box, "Bu klasörde ara").</summary>
    public void ApplyPending()
    {
        var (scope, query) = Session.PendingSearch;
        Session.PendingSearch = (null, null);
        if (scope is not null) { ScopePath = scope; ResolveScope(); }
        if (query is not null)
        {
            bool same = QueryText == query;
            QueryText = query;
            if (same) Schedule(immediate: true);
        }
        else if (scope is not null) Schedule(immediate: true);
    }

    private void ResolveScope()
    {
        scopeNode = FileIndex.RootNode;
        if (ScopePath is null || Session.Index is not { } index) return;
        int node = index.FindByPath(ScopePath);
        if (node >= 0) scopeNode = node;
        else ScopePath = null;
    }

    [RelayCommand]
    private void ClearScope()
    {
        ScopePath = null;
        scopeNode = FileIndex.RootNode;
        Schedule(immediate: true);
    }

    [RelayCommand]
    private void ClearQuery() => QueryText = "";

    [RelayCommand]
    private void ResetFilters()
    {
        SizeFilter = 0;
        DateFilter = 0;
        TypeFilter = 0;
    }

    public void InsertSyntax(SyntaxHint hint) => QueryText = (QueryText.TrimEnd() + " " + hint.Code).Trim();

    public void ApplySaved(SavedSearchItem item)
    {
        ModeIndex = item.Mode switch { "Regex" => 1, "Text" => 2, _ => 0 };
        ResetFilters();
        QueryText = item.Query;
    }

    public void SaveCurrent(string name)
    {
        if (string.IsNullOrWhiteSpace(QueryText)) return;
        var saved = new SavedSearch
        {
            Name = string.IsNullOrWhiteSpace(name) ? QueryText : name.Trim(),
            Query = QueryText,
            Mode = ModeIndex switch { 1 => "Regex", 2 => "Text", _ => "Glob" },
        };
        SettingsStore.Current.SavedSearches.Insert(0, saved);
        SettingsStore.Save();
        SavedSearches.Insert(0, new SavedSearchItem(saved));
    }

    public void DeleteSaved(SavedSearchItem item)
    {
        SettingsStore.Current.SavedSearches.Remove(item.Model);
        SettingsStore.Save();
        SavedSearches.Remove(item);
    }

    public void UpdateSelection(IReadOnlyList<ResultRow> selected)
    {
        SelectedCount = selected.Count;
        if (Session.Index is not { } index || selected.Count == 0) { SelectionText = ""; return; }
        long bytes = SearchEngine.NonOverlappingTotal(index, selected.Select(r => r.Node).ToList());
        SelectionText = $"{Ui.Count(selected.Count)} seçili · {Ui.Size(bytes)}";
    }

    // ---- running ----------------------------------------------------------------------------

    private string ComposeQuery()
    {
        var parts = new List<string>(4) { QueryText };
        parts.Add(SizeFilter switch { 1 => "size:>100mb", 2 => "size:>1gb", 3 => "size:>10gb", 4 => "size:<1mb", 5 => "size:empty", _ => "" });
        parts.Add(DateFilter switch { 1 => "age:<7d", 2 => "age:<30d", 3 => "age:<1y", 4 => "age:>1y", 5 => "age:>3y", _ => "" });
        parts.Add(TypeFilter switch
        {
            1 => "type:video", 2 => "type:audio", 3 => "type:image", 4 => "type:document", 5 => "type:archive",
            6 => "type:diskimage", 7 => "type:code", 8 => "type:app", 9 => "type:game", 10 => "type:system", 11 => "kind:dir",
            _ => "",
        });
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }

    private void Schedule(bool immediate = false)
    {
        pending?.Cancel();
        pending = new CancellationTokenSource();
        _ = RunAsync(pending.Token, immediate ? 0 : 250);
    }

    private async Task RunAsync(CancellationToken ct, int delay)
    {
        try
        {
            if (delay > 0) await Task.Delay(delay, ct);
            if (Session.Index is not { } index) { Clear("Önce bir tarama yapın."); return; }

            string text = ComposeQuery();
            var mode = ModeIndex switch { 1 => QueryMode.Regex, 2 => QueryMode.Text, _ => QueryMode.Glob };
            var query = Query.Parse(text, mode, new QueryOptions
            {
                CaseSensitive = CaseSensitive,
                MatchFullPath = FullPath,
                IncludeDirectories = IncludeDirectories || TypeFilter == 11,
            });

            Tokens.Clear();
            foreach (var t in query.Tokens) Tokens.Add(new TokenChip(t));
            Error = query.Error;
            if (!query.IsValid) { Clear("Sorgu hatalı."); return; }
            if (query.IsEmpty) { Clear("Bir desen yazın ya da filtre seçin."); return; }

            IsSearching = true;
            int scope = scopeNode;
            var result = await Task.Run(() => SearchEngine.Search(index, query, scope, MaxRows, ct), ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(index, Session.Index)) return;

            lastResult = result;
            searched = true;
            Fill();
            Summary = $"{Ui.Count(result.TotalMatches)} sonuç · {Ui.Size(result.TotalBytes)}";
            Detail = $"{Ui.Count(index.Count)} öğe {Core.Formatting.SizeFormatter.FormatDuration(result.Elapsed)} içinde arandı" +
                     (result.Truncated ? $" · en büyük {Ui.Count(MaxRows)} sonuç listeleniyor" : "");
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!ct.IsCancellationRequested) IsSearching = false;
        }
    }

    private void Clear(string summary)
    {
        lastResult = SearchResult.Empty;
        searched = false;
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmpty));
        Summary = summary;
        Detail = "";
        IsSearching = false;
    }

    private void Fill()
    {
        Results.Clear();
        if (Session.Index is { } index)
        {
            IEnumerable<int> nodes = lastResult.Nodes;
            nodes = (Columns.SortKey, Columns.SortDescending) switch
            {
                ("name", false) => nodes.OrderBy(index.GetName, StringComparer.CurrentCultureIgnoreCase),
                ("name", true) => nodes.OrderByDescending(index.GetName, StringComparer.CurrentCultureIgnoreCase),
                ("folder", false) => nodes.OrderBy(n => index.GetFullPath(index.Parent(n)), StringComparer.CurrentCultureIgnoreCase),
                ("folder", true) => nodes.OrderByDescending(n => index.GetFullPath(index.Parent(n)), StringComparer.CurrentCultureIgnoreCase),
                ("modified", false) => nodes.OrderBy(index.ModifiedTicks),
                ("modified", true) => nodes.OrderByDescending(index.ModifiedTicks),
                ("size", false) => nodes.OrderBy(index.Size),
                _ => nodes,
            };
            foreach (int n in nodes) Results.Add(ResultRow.Create(index, n));
        }
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmpty));
        SelectedCount = 0;
        SelectionText = "";
    }

    // ---- actions on the selection -----------------------------------------------------------

    public void Reveal(ResultRow row) => FileActions.Reveal(row.FullPath);

    public void ShowInTree(ResultRow row)
    {
        Session.PendingReveal = row.FullPath;
        App.MainWindow?.Navigate("tree");
    }

    public void CopyPaths(IReadOnlyList<ResultRow> rows) => FileActions.CopyText(string.Join(Environment.NewLine, rows.Select(r => r.FullPath)));

    public Task ExportAsync() =>
        Session.Index is null ? Task.CompletedTask
            : FileActions.ExportNodesCsvAsync(Results.Select(r => r.Node).ToList(), "arama sonuçları");
}

public sealed partial class ResultRow : ObservableObject
{
    public int Node { get; init; }
    public string Name { get; init; } = "";
    public string Folder { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string ModifiedText { get; init; } = "";
    public string Badge { get; init; } = "";
    public Color BadgeColor { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsFile => !IsDirectory;
    public string Attributes { get; init; } = "";
    public bool IsLarge { get; init; }

    public static ResultRow Create(FileIndex index, int node)
    {
        bool dir = index.IsDirectory(node);
        var ext = index.ExtensionSpan(node);
        string folder = index.GetFullPath(index.Parent(node));
        return new ResultRow
        {
            Node = node,
            Name = index.GetName(node),
            Folder = folder,
            FullPath = index.GetFullPath(node),
            SizeText = Ui.Size(index.Size(node)),
            ModifiedText = Ui.ShortDate(index.ModifiedUtc(node)),
            Badge = dir ? "" : ext.IsEmpty ? "—" : ext.Length > 5 ? ext[..5].ToString().ToUpperInvariant() : ext.ToString().ToUpperInvariant(),
            BadgeColor = Ui.CategoryColor(FileCategories.Classify(index, node)),
            IsDirectory = dir,
            Attributes = CsvExporter.DescribeAttributes(index, node),
            IsLarge = index.Size(node) >= 1L << 30,
        };
    }
}

public sealed record TokenChip(QueryToken Token)
{
    public string Kind => (Token.Negated ? "hariç " : "") + Token.KindLabel;
    public string Value => Token.Value;
}

public sealed record SyntaxHint(string Code, string Description);

public sealed class SavedSearchItem(SavedSearch model)
{
    public SavedSearch Model { get; } = model;
    public string Name => Model.Name;
    public string Query => Model.Query;
    public string Mode => Model.Mode;
    public string ModeLabel => Model.Mode switch { "Regex" => "Regex", "Text" => "Metin", _ => "Joker" };
}
