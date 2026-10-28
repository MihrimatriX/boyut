using System.Collections.ObjectModel;
using Boyut.App.Services;
using Boyut.Core.Analysis;
using Boyut.Core.Model;
using Boyut.Platform.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Boyut.App.ViewModels;

public sealed partial class CleanupViewModel : ObservableObject
{
    private CancellationTokenSource? analysis;
    private CancellationTokenSource? duplicateScan;

    public static CleanupViewModel Current { get; } = new();

    private CleanupViewModel()
    {
        Session.IndexChanged += (_, _) => _ = AnalyzeAsync();
        _ = AnalyzeAsync();
    }

    public ScanSession Session => ScanSession.Current;

    public ObservableCollection<FindingRow> Findings { get; } = [];
    public ObservableCollection<CategoryRow> Categories { get; } = [];
    public ObservableCollection<AgeRow> Ages { get; } = [];
    public ObservableCollection<ResultRow> LargestFiles { get; } = [];
    public ObservableCollection<DuplicateRow> Duplicates { get; } = [];

    public IReadOnlyList<string> DuplicateSizes { get; } = ["1 MB'tan büyük", "10 MB'tan büyük", "100 MB'tan büyük", "1 GB'tan büyük"];

    [ObservableProperty] public partial bool IsAnalyzing { get; private set; }
    [ObservableProperty] public partial string SafeText { get; private set; } = "";
    [ObservableProperty] public partial string CautionText { get; private set; } = "";
    [ObservableProperty] public partial string SelectedText { get; private set; } = "";
    [ObservableProperty] public partial double SelectedPercent { get; private set; }
    [ObservableProperty] public partial bool HasSelection { get; private set; }
    [ObservableProperty] public partial string TotalText { get; private set; } = "";

    [ObservableProperty] public partial int DuplicateSizeIndex { get; set; }
    [ObservableProperty] public partial bool IsFindingDuplicates { get; private set; }
    [ObservableProperty] public partial string DuplicateStatus { get; private set; } = "İçeriği birebir aynı dosyaları bulur (boyut → baş/son → tam içerik özeti). Bulut dosyaları indirilmez.";
    [ObservableProperty] public partial double DuplicateProgress { get; private set; }

    public bool HasIndex => Session.HasIndex;
    public bool HasDuplicates => Duplicates.Count > 0;

    // ---- analysis ---------------------------------------------------------------------------

    private async Task AnalyzeAsync()
    {
        analysis?.Cancel();
        duplicateScan?.Cancel();
        analysis = new CancellationTokenSource();
        var ct = analysis.Token;

        Findings.Clear();
        Categories.Clear();
        Ages.Clear();
        LargestFiles.Clear();
        Duplicates.Clear();
        OnPropertyChanged(nameof(HasIndex));
        OnPropertyChanged(nameof(HasDuplicates));
        UpdateSelection();
        if (Session.Index is not { } index) return;

        IsAnalyzing = true;
        try
        {
            var now = DateTime.UtcNow;
            var (findings, types, ages, largest) = await Task.Run(() => (
                CleanupAnalyzer.Analyze(index, nowUtc: now, cancellationToken: ct),
                TypeStatistics.Compute(index),
                TypeStatistics.AgeDistribution(index, now),
                TypeStatistics.LargestFiles(index, count: 15)), ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(index, Session.Index)) return;

            foreach (var f in findings) Findings.Add(new FindingRow(f, index, this));

            long total = Math.Max(1, types.TotalBytes);
            TotalText = Ui.Size(types.TotalBytes);
            foreach (var c in types.Categories)
                Categories.Add(new CategoryRow(c.Name, Ui.Size(c.Bytes), c.Bytes * 100.0 / total, Ui.Percent((double)c.Bytes / total), Ui.Count(c.Files) + " dosya", Ui.CategoryColor(c.Category)));

            long ageMax = Math.Max(1, ages.Max(a => a.Bytes));
            foreach (var a in ages)
                Ages.Add(new AgeRow(a.Label, Ui.Size(a.Bytes), a.Bytes * 100.0 / ageMax, Ui.Count(a.Files) + " dosya"));

            foreach (int n in largest) LargestFiles.Add(ResultRow.Create(index, n));

            long safe = findings.Where(f => f.Rule.Safety == CleanupSafety.Safe).Sum(f => f.Bytes);
            long caution = findings.Where(f => f.Rule.Safety == CleanupSafety.Caution).Sum(f => f.Bytes);
            SafeText = Ui.Size(safe);
            CautionText = Ui.Size(caution);
            UpdateSelection();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!ct.IsCancellationRequested) IsAnalyzing = false;
        }
    }

    internal void UpdateSelection()
    {
        if (Session.Index is not { } index) { SelectedText = ""; HasSelection = false; return; }
        var nodes = Findings.Where(f => f.IsChecked && f.CanSelect).SelectMany(f => f.Nodes).ToList();
        long bytes = Core.Search.SearchEngine.NonOverlappingTotal(index, nodes);
        long recoverable = Findings.Where(f => f.CanSelect).Sum(f => f.Bytes);
        SelectedText = Ui.Size(bytes);
        SelectedPercent = recoverable > 0 ? bytes * 100.0 / recoverable : 0;
        HasSelection = nodes.Count > 0;
    }

    public async Task CleanSelectedAsync(XamlRoot root)
    {
        var chosen = Findings.Where(f => f.IsChecked && f.CanSelect).ToList();
        if (chosen.Count == 0 || Session.Index is not { } before) return;

        // The Recycle Bin itself is emptied, not "moved to the Recycle Bin".
        var bin = chosen.FirstOrDefault(f => f.RuleId == "recycle");
        if (bin is not null)
        {
            chosen.Remove(bin);
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "Geri Dönüşüm Kutusu boşaltılsın mı?",
                Content = $"{bin.SizeText} kalıcı olarak silinecek. Bu işlem geri alınamaz.",
                PrimaryButtonText = "Boşalt",
                CloseButtonText = "Vazgeç",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await FileActions.Themed(dialog).ShowAsync() == ContentDialogResult.Primary && Session.Index is { } index)
            {
                string drive = Path.GetPathRoot(index.Metadata.RootPath) ?? index.Metadata.RootPath;
                if (WindowsShell.EmptyRecycleBin(drive, App.MainWindow?.Hwnd ?? 0))
                    await Session.RemoveAsync(bin.Nodes);
            }
        }

        // Paths, not node numbers: emptying the bin above rebuilt the index.
        if (chosen.Count > 0)
            await FileActions.RecyclePathsAsync(chosen.SelectMany(f => f.Nodes).Select(before.GetFullPath).ToList(), root);
    }

    // ---- duplicates -------------------------------------------------------------------------

    [RelayCommand(AllowConcurrentExecutions = true)] // the same button stops a running search
    private async Task FindDuplicates()
    {
        if (Session.Index is not { } index) return;
        if (IsFindingDuplicates) { duplicateScan?.Cancel(); return; }

        duplicateScan = new CancellationTokenSource();
        var ct = duplicateScan.Token;
        IsFindingDuplicates = true;
        Duplicates.Clear();
        OnPropertyChanged(nameof(HasDuplicates));
        DuplicateProgress = 0;

        long min = DuplicateSizeIndex switch { 1 => 10L << 20, 2 => 100L << 20, 3 => 1L << 30, _ => 1L << 20 };
        var progress = new Progress<DuplicateProgress>(p =>
        {
            DuplicateStatus = p.Total > 0 ? $"{p.Phase}: {Ui.Count(p.Done)} / {Ui.Count(p.Total)}" : p.Phase;
            DuplicateProgress = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
        });

        try
        {
            var groups = await DuplicateFinder.FindAsync(index, options: new DuplicateOptions
            {
                MinimumSize = min,
                IdentityProvider = WindowsShell.GetFileIdentity,
            }, progress: progress, cancellationToken: ct);
            if (!ReferenceEquals(index, Session.Index)) return;

            foreach (var g in groups.Take(200)) Duplicates.Add(new DuplicateRow(g, index));
            long wasted = groups.Sum(g => g.Wasted);
            DuplicateStatus = groups.Count == 0
                ? "Kopya bulunamadı."
                : $"{Ui.Count(groups.Count)} grup · {Ui.Size(wasted)} boşa harcanıyor" + (groups.Count > 200 ? " · en büyük 200 grup gösteriliyor" : "");
        }
        catch (OperationCanceledException)
        {
            DuplicateStatus = "Kopya araması durduruldu.";
        }
        finally
        {
            IsFindingDuplicates = false;
            OnPropertyChanged(nameof(HasDuplicates));
        }
    }

    public void Inspect(int node)
    {
        if (Session.Index is not { } index) return;
        Session.PendingReveal = index.GetFullPath(node);
        App.MainWindow?.Navigate("tree");
    }
}

public sealed partial class FindingRow : ObservableObject
{
    private readonly CleanupViewModel owner;

    public FindingRow(CleanupFinding finding, FileIndex index, CleanupViewModel owner)
    {
        this.owner = owner;
        var rule = finding.Rule;
        RuleId = rule.Id;
        Title = rule.Title;
        Description = rule.Description;
        Advice = rule.Advice ?? "";
        Group = rule.Group;
        SafetyLabel = Ui.SafetyLabel(rule.Safety);
        SafetyColor = Ui.SafetyColor(rule.Safety);
        CanSelect = rule.Safety != CleanupSafety.Keep;
        Bytes = finding.Bytes;
        SizeText = Ui.Size(finding.Bytes);
        Nodes = finding.Nodes;
        Locations = finding.Nodes.Length == 1
            ? "1 konum"
            : $"{Ui.Count(finding.Nodes.Length)} konum";
        FilesText = Ui.Count(finding.Files) + " dosya";
        AdminText = rule.RequiresAdmin ? "Yönetici izni gerekebilir" : "";
        Paths = finding.Nodes.Take(8).Select(n => new PathRow(n, index.GetFullPath(n), Ui.Size(index.Size(n)), owner)).ToList();
        MoreText = finding.Nodes.Length > 8 ? $"… ve {Ui.Count(finding.Nodes.Length - 8)} konum daha" : "";
        IsChecked = rule.Safety == CleanupSafety.Safe && !rule.RequiresAdmin;
    }

    public string RuleId { get; }
    public string Title { get; }
    public string Description { get; }
    public string Advice { get; }
    public string Group { get; }
    public string SafetyLabel { get; }
    public Color SafetyColor { get; }
    public bool CanSelect { get; }
    public long Bytes { get; }
    public string SizeText { get; }
    public int[] Nodes { get; }
    public string Locations { get; }
    public string FilesText { get; }
    public string AdminText { get; }
    public IReadOnlyList<PathRow> Paths { get; }
    public string MoreText { get; }
    public bool HasAdvice => Advice.Length > 0;

    [ObservableProperty] public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => owner.UpdateSelection();
}

public sealed partial class PathRow(int node, string path, string size, CleanupViewModel owner) : ObservableObject
{
    public string Path { get; } = path;
    public string SizeText { get; } = size;

    [RelayCommand]
    private void Inspect() => owner.Inspect(node);

    [RelayCommand]
    private void Reveal() => FileActions.Reveal(Path);
}

public sealed record CategoryRow(string Name, string SizeText, double Percent, string PercentText, string FilesText, Color Color);

public sealed record AgeRow(string Label, string SizeText, double Percent, string FilesText);

public sealed partial class DuplicateRow : ObservableObject
{
    public DuplicateRow(DuplicateGroup group, FileIndex index)
    {
        // Keep the oldest copy (most likely the original); the rest can go.
        var ordered = group.Nodes.OrderBy(index.ModifiedTicks).ThenBy(n => index.GetFullPath(n).Length).ToArray();
        Keep = ordered[0];
        Extra = ordered[1..];
        Title = $"{index.GetName(Keep)}";
        Summary = $"{Ui.Size(group.Size)} × {group.Nodes.Length} · {Ui.Size(group.Wasted)} boşa";
        Paths = ordered.Select((n, i) => (i == 0 ? "Korunacak:  " : "Kopya:  ") + index.GetFullPath(n)).ToList();
    }

    public int Keep { get; }
    public int[] Extra { get; }
    public string Title { get; }
    public string Summary { get; }
    public IReadOnlyList<string> Paths { get; }
}
