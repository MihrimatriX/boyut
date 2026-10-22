using Boyut.Core.Formatting;
using Boyut.Core.Model;
using Boyut.Core.Persistence;
using Boyut.Core.Scanning;
using Boyut.Platform.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Boyut.App.Services;

/// <summary>
/// The one scan the whole app looks at. Pages subscribe to <see cref="IndexChanged"/>;
/// all members are used from the UI thread.
/// </summary>
public sealed partial class ScanSession : ObservableObject
{
    private CancellationTokenSource? scanCts;

    public static ScanSession Current { get; } = new();

    [ObservableProperty] public partial FileIndex? Index { get; private set; }
    [ObservableProperty] public partial bool IsScanning { get; private set; }
    [ObservableProperty] public partial string ScanTarget { get; private set; } = "";
    [ObservableProperty] public partial string ProgressText { get; private set; } = "";
    [ObservableProperty] public partial string ProgressDetail { get; private set; } = "";
    [ObservableProperty] public partial double ProgressValue { get; private set; }
    [ObservableProperty] public partial bool ProgressIndeterminate { get; private set; } = true;

    /// <summary>Last problem worth telling the user about (shown in an InfoBar).</summary>
    [ObservableProperty] public partial string? Notice { get; set; }
    [ObservableProperty] public partial bool NoticeIsError { get; set; }

    public bool HasIndex => Index is not null;
    public string RootLabel => Index is null ? "" : Index.Metadata.RootPath;

    /// <summary>Raised after a scan, a snapshot load or a deletion replaced <see cref="Index"/>.</summary>
    public event EventHandler? IndexChanged;

    /// <summary>Path the tree should reveal next time it is shown ("İncele" from other pages).</summary>
    public string? PendingReveal { get; set; }

    /// <summary>Scope + query handed to the search page ("Bu klasörde ara").</summary>
    public (string? ScopePath, string? Query) PendingSearch { get; set; }

    partial void OnIndexChanged(FileIndex? value)
    {
        OnPropertyChanged(nameof(HasIndex));
        OnPropertyChanged(nameof(RootLabel));
        IndexChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised on the UI thread after the recent-scans list changed.</summary>
    public event EventHandler? RecentChanged;

    public void Cancel() => scanCts?.Cancel();

    /// <summary>Root the "Değişimler" page should open next time it is shown (toast, recent list).</summary>
    public string? PendingChangesRoot { get; set; }

    private async Task RememberAsync(FileIndex index)
    {
        bool saved = await Task.Run(() =>
        {
            try
            {
                SettingsStore.History.Save(index);
                SettingsStore.History.Prune(index.Metadata.RootPath, DateTime.UtcNow);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        });
        if (saved) RecentChanged?.Invoke(this, EventArgs.Empty);
    }

    private static ScanOptions CurrentOptions() => new()
    {
        IncludeHidden = SettingsStore.Current.IncludeHidden,
        FollowReparsePoints = SettingsStore.Current.FollowLinks,
        IncludeAlternateStreams = SettingsStore.Current.IncludeStreams,
    };

    /// <summary>
    /// Brings the current scan up to date from the NTFS change journal (no admin, usually under a second).
    /// When the journal cannot vouch for the gap, does a full scan — or nothing, if <paramref name="fallbackToScan"/> is false.
    /// </summary>
    public async Task<bool> RefreshAsync(bool fallbackToScan = true)
    {
        if (IsScanning || Index is not { } index) return false;
        string root = index.Metadata.RootPath;
        FileIndex? updated = null;
        if (index.Metadata.Usn is not null && OperatingSystem.IsWindows() && Directory.Exists(root))
        {
            IsScanning = true;
            ScanTarget = root;
            ProgressIndeterminate = true;
            ProgressText = "Değişiklik günlüğü okunuyor…";
            ProgressDetail = "";
            try { updated = await Task.Run(() => UsnRefresher.TryRefresh(index, CurrentOptions())); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            finally { IsScanning = false; }
        }
        if (updated is null) return fallbackToScan && await ScanAsync(root);
        if (!ReferenceEquals(Index, index)) return false; // something else replaced the index meanwhile

        long delta = updated.TotalSize - index.TotalSize;
        Index = updated;
        Notice = $"Değişiklik günlüğüyle {SizeFormatter.FormatDuration(updated.Metadata.Duration)} içinde güncellendi" +
                 (delta == 0 && updated.TotalFiles == index.TotalFiles ? "; değişiklik yok." : $" ({Ui.SignedSize(delta)}).");
        NoticeIsError = false;
        if (delta != 0 || updated.TotalFiles != index.TotalFiles) _ = RememberAsync(updated);
        return true;
    }

    public async Task<bool> ScanAsync(string root)
    {
        if (IsScanning) return false;
        var settings = SettingsStore.Current;
        var options = CurrentOptions();

        scanCts = new CancellationTokenSource();
        var ct = scanCts.Token;
        IsScanning = true;
        ScanTarget = root;
        ProgressValue = 0;
        ProgressIndeterminate = true;
        ProgressText = "Tarama başlıyor…";
        ProgressDetail = "";
        Notice = null;

        // Created on the UI thread, so reports arrive on the UI thread.
        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressIndeterminate = p.Fraction is null;
            if (p.Fraction is { } f) ProgressValue = f * 100;
            ProgressText = $"{p.Phase ?? "Taranıyor"} · {Ui.Count(p.Items)} öğe" + (p.Bytes > 0 ? $" · {Ui.Size(p.Bytes)}" : "");
            ProgressDetail = p.CurrentPath ?? "";
        });

        try
        {
            FileIndex index;
            bool mftPossible = settings.UseMft && ScannerFactory.IsLocalNtfs(root);
            if (mftPossible && Elevation.IsElevated)
            {
                index = await new MftScanner().ScanAsync(root, options, progress, ct);
            }
            else if (mftPossible)
            {
                ProgressText = "Yönetici izni bekleniyor…";
                try
                {
                    index = await ElevatedScanClient.ScanAsync(root, options, progress, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // UAC declined: fall back to the portable scanner instead of failing.
                    Notice = "Yönetici izni verilmedi; klasör klasör tarandı (daha yavaş).";
                    NoticeIsError = false;
                    index = await ScannerFactory.Create(ScanEngine.Managed, root).ScanAsync(root, options, progress, ct);
                }
                catch (IOException ex)
                {
                    Notice = $"Hızlı tarama başarısız oldu ({ex.Message}); klasör taraması kullanıldı.";
                    NoticeIsError = false;
                    index = await ScannerFactory.Create(ScanEngine.Managed, root).ScanAsync(root, options, progress, ct);
                }
            }
            else
            {
                index = await ScannerFactory.Create(ScanEngine.Managed, root).ScanAsync(root, options, progress, ct);
            }

            if (index.Metadata.ErrorCount > 0 && Notice is null)
            {
                Notice = $"{Ui.Count(index.Metadata.ErrorCount)} klasör okunamadı (erişim engellendi); toplamlar biraz eksik olabilir.";
                NoticeIsError = false;
            }

            Index = index;
            _ = RememberAsync(index);
            return true;
        }
        catch (OperationCanceledException)
        {
            Notice = "Tarama iptal edildi.";
            NoticeIsError = false;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Notice = ex.Message;
            NoticeIsError = true;
            return false;
        }
        finally
        {
            IsScanning = false;
            scanCts.Dispose();
            scanCts = null;
        }
    }

    public async Task<bool> OpenSnapshotAsync(string file)
    {
        try
        {
            var index = await Task.Run(() => SnapshotSerializer.Load(file));
            Index = index;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            Notice = "Tarama dosyası açılamadı: " + ex.Message;
            NoticeIsError = true;
            return false;
        }
    }

    /// <summary>Drops deleted items from the index so every view updates without a rescan.</summary>
    public async Task RemoveAsync(IReadOnlyCollection<int> nodes)
    {
        if (Index is not { } index || nodes.Count == 0) return;
        long before = index.TotalSize;
        var updated = await Task.Run(() => index.Without(nodes));
        Index = updated;
        Notice = $"{Ui.Size(before - updated.TotalSize)} Geri Dönüşüm Kutusu'na taşındı.";
        NoticeIsError = false;
    }

    public string Summary()
    {
        if (Index is not { } i) return "";
        var m = i.Metadata;
        return $"{SizeFormatter.Format(i.TotalSize)} · {Ui.Count(i.TotalFiles)} dosya · {(m.Engine == "mft" ? "MFT" : "klasör taraması")} · {SizeFormatter.FormatDuration(m.Duration)}";
    }
}
