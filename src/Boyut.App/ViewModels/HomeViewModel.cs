using System.Collections.ObjectModel;
using Boyut.App.Services;
using Boyut.Core.Persistence;
using Boyut.Platform.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Boyut.App.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    public static HomeViewModel Current { get; } = new();

    private HomeViewModel()
    {
        var s = SettingsStore.Current;
        UseMft = s.UseMft;
        IncludeHidden = s.IncludeHidden;
        FollowLinks = s.FollowLinks;
        IncludeStreams = s.IncludeStreams;
        ShowAdminTip = s.ShowAdminTip && !Elevation.IsElevated;
        Session.RecentChanged += (_, _) => Refresh();
        Refresh();
    }

    public ScanSession Session => ScanSession.Current;
    public ObservableCollection<DriveItem> Drives { get; } = [];
    public ObservableCollection<RecentItem> Recent { get; } = [];

    [ObservableProperty] public partial string PathText { get; set; } = "";
    [ObservableProperty] public partial bool UseMft { get; set; }
    [ObservableProperty] public partial bool IncludeHidden { get; set; }
    [ObservableProperty] public partial bool FollowLinks { get; set; }
    [ObservableProperty] public partial bool IncludeStreams { get; set; }
    [ObservableProperty] public partial bool ShowAdminTip { get; set; }

    public bool HasRecent => Recent.Count > 0;

    partial void OnUseMftChanged(bool value) { SettingsStore.Current.UseMft = value; SettingsStore.Save(); }
    partial void OnIncludeHiddenChanged(bool value) { SettingsStore.Current.IncludeHidden = value; SettingsStore.Save(); }
    partial void OnFollowLinksChanged(bool value) { SettingsStore.Current.FollowLinks = value; SettingsStore.Save(); }
    partial void OnIncludeStreamsChanged(bool value) { SettingsStore.Current.IncludeStreams = value; SettingsStore.Save(); }
    partial void OnShowAdminTipChanged(bool value)
    {
        if (value) return;
        SettingsStore.Current.ShowAdminTip = false;
        SettingsStore.Save();
    }

    private const int MaxRecent = 12;
    private bool loading, reloadPending;

    public void Refresh() => _ = LoadAsync();

    /// <summary>IsReady / TotalSize can block for seconds on a sleeping network or optical drive; history reads files.</summary>
    private async Task LoadAsync()
    {
        if (loading) { reloadPending = true; return; }
        loading = true;
        try
        {
            var (drives, roots) = await Task.Run(() =>
            {
                SettingsStore.MigrateLegacySnapshots();
                var roots = SettingsStore.History.ListRoots();
                var list = new List<DriveItem>();
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady) continue;
                        var last = roots.FirstOrDefault(r => string.Equals(r.Latest.RootPath.TrimEnd('\\'), drive.Name.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                        list.Add(DriveItem.From(drive, last.Latest?.ScannedAtUtc));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                return (list, roots);
            });
            Drives.Clear();
            foreach (var d in drives) Drives.Add(d);
            Recent.Clear();
            foreach (var (latest, count) in roots.Take(MaxRecent)) Recent.Add(new RecentItem(latest, count));
            OnPropertyChanged(nameof(HasRecent));
        }
        finally
        {
            loading = false;
        }
        if (reloadPending) { reloadPending = false; Refresh(); }
    }

    [RelayCommand]
    private void ScanPath()
    {
        string path = PathText.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':') path += "\\";
        if (!Directory.Exists(path))
        {
            Session.Notice = $"Klasör bulunamadı: {path}";
            Session.NoticeIsError = true;
            return;
        }
        App.MainWindow?.StartScan(path);
    }

    [RelayCommand]
    private async Task PickFolder()
    {
        if (App.MainWindow is not { } window) return;
        if (await window.PickFolderAsync() is { } path) window.StartScan(path);
    }

    [RelayCommand]
    private void RestartElevated()
    {
        if (Elevation.RestartElevated()) Microsoft.UI.Xaml.Application.Current.Exit();
    }
}

public sealed partial class DriveItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Root { get; init; }
    public required string Detail { get; init; }
    public required string Glyph { get; init; }
    public double UsedPercent { get; init; }
    public string UsedText { get; init; } = "";
    public string FreeText { get; init; } = "";
    public bool IsNtfs { get; init; }

    public string LastScanText { get; init; } = "Henüz taranmadı";

    public string EngineHint => IsNtfs ? "MFT ile hızlı tarama" : "Klasör taraması";

    [RelayCommand]
    private void Scan() => App.MainWindow?.StartScan(Root);

    public static DriveItem From(DriveInfo drive, DateTime? lastScanUtc)
    {
        string letter = drive.Name.TrimEnd('\\');
        string label = drive.VolumeLabel;
        if (string.IsNullOrWhiteSpace(label))
        {
            label = drive.DriveType switch
            {
                DriveType.Removable => "USB Sürücü",
                DriveType.Network => "Ağ sürücüsü",
                DriveType.CDRom => "DVD sürücüsü",
                DriveType.Ram => "RAM disk",
                _ => "Yerel Disk",
            };
        }
        string type = drive.DriveType switch
        {
            DriveType.Fixed => "Sabit disk",
            DriveType.Removable => "Çıkarılabilir",
            DriveType.Network => "Ağ",
            DriveType.CDRom => "Optik",
            _ => drive.DriveType.ToString(),
        };
        long total = drive.TotalSize, free = drive.AvailableFreeSpace, used = total - drive.TotalFreeSpace;
        return new DriveItem
        {
            Name = $"{label} ({letter})",
            Root = drive.Name,
            Detail = $"{type} · {drive.DriveFormat}",
            Glyph = drive.DriveType switch
            {
                DriveType.Removable => "",
                DriveType.Network => "",
                DriveType.CDRom => "",
                _ => "",
            },
            UsedPercent = total > 0 ? used * 100.0 / total : 0,
            UsedText = $"{Ui.Size(used)} kullanılıyor",
            FreeText = $"{Ui.Size(free)} boş / {Ui.Size(total)}",
            IsNtfs = string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
            LastScanText = lastScanUtc is { } t ? "Son tarama: " + Ui.Relative(t) : "Henüz taranmadı",
        };
    }
}

public sealed partial class RecentItem(HistoryEntry latest, int scanCount) : ObservableObject
{
    public string Path => latest.RootPath;
    public string When => Ui.Relative(latest.ScannedAtUtc) + $" · {latest.Header.Metadata.Duration.TotalSeconds:0.#} sn";
    public string SizeText => Ui.Size(latest.Header.TotalSize);
    public string FilesText => Ui.Count(latest.Header.TotalFiles) + " dosya";
    public string EngineText => latest.Header.Metadata.Engine == "mft" ? "MFT" : "Klasör";
    public bool HasChanges => scanCount >= 2;

    [RelayCommand]
    private void Open()
    {
        if (File.Exists(latest.File)) App.MainWindow?.OpenSnapshot(latest.File, refresh: true);
        else App.MainWindow?.StartScan(latest.RootPath);
    }

    [RelayCommand]
    private void Rescan() => App.MainWindow?.StartScan(latest.RootPath);

    [RelayCommand]
    private void ShowChanges()
    {
        ScanSession.Current.PendingChangesRoot = latest.RootPath;
        App.MainWindow?.Navigate("changes");
    }
}
