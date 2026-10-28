using System.Globalization;
using Boyut.Core.Model;
using Boyut.Core.Persistence;
using Boyut.Platform.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace Boyut.App.Services;

/// <summary>Actions shared by the tree, search and cleanup pages.</summary>
public static class FileActions
{
    private static ScanSession Session => ScanSession.Current;

    public static void Reveal(string path)
    {
        try { WindowsShell.RevealInExplorer(path); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Fail(ex); }
    }

    public static void Open(string path)
    {
        try { WindowsShell.Open(path); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Fail(ex); }
    }

    public static void OpenTerminal(string folder)
    {
        try { WindowsShell.OpenTerminal(folder); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Fail(ex); }
    }

    public static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    /// <summary>
    /// Asks for confirmation, moves the items to the Recycle Bin and drops whatever really
    /// disappeared from the index (partial success is handled too).
    /// </summary>
    public static Task<bool> RecycleAsync(IReadOnlyList<int> nodes, XamlRoot? root)
    {
        if (Session.Index is not { } index) return Task.FromResult(false);
        // Resolve to paths right away: node numbers change whenever the index is rebuilt.
        return RecyclePathsAsync(nodes.Where(n => n > 0 && n < index.Count).Select(index.GetFullPath).ToList(), root);
    }

    public static async Task<bool> RecyclePathsAsync(IReadOnlyList<string> requested, XamlRoot? root)
    {
        if (Session.Index is not { } index || requested.Count == 0 || root is null) return false;
        var targets = requested.Select(index.FindByPath).Where(n => n > FileIndex.RootNode).Distinct().ToList();
        // Drop children of selected folders: they go with their parent.
        targets = targets.Where(n => !targets.Any(o => o != n && index.IsAncestorOrSelf(o, n))).ToList();
        if (targets.Count == 0) return false;

        long bytes = targets.Sum(index.EffectiveSize);
        int files = targets.Sum(index.FileCount);
        string what = targets.Count == 1
            ? $"“{index.GetName(targets[0])}”"
            : $"{targets.Count.ToString(CultureInfo.CurrentCulture)} öğe";

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Geri Dönüşüm Kutusu'na taşınsın mı?",
            Content = $"{what} ({Ui.Size(bytes)}, {Ui.Count(files)} dosya) Geri Dönüşüm Kutusu'na taşınacak. " +
                      "Gerekirse oradan geri alabilirsiniz. Geri Dönüşüm Kutusu'na sığmayan çok büyük öğeler için Windows ayrıca uyarır.",
            PrimaryButtonText = "Taşı",
            CloseButtonText = "Vazgeç",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await Themed(dialog).ShowAsync() != ContentDialogResult.Primary) return false;

        var paths = targets.Select(index.GetFullPath).ToList();
        nint owner = App.MainWindow?.Hwnd ?? 0;
        // The shell operation can take minutes for big folders: run it on its own STA thread.
        var result = await RunSta(() => WindowsShell.SendToRecycleBin(paths, owner));
        if (!ReferenceEquals(index, Session.Index)) return false;

        var gone = targets.Where((n, i) => !File.Exists(paths[i]) && !Directory.Exists(paths[i])).ToList();
        if (gone.Count > 0) await Session.RemoveAsync(gone);
        if (!result.Success && gone.Count < targets.Count)
        {
            Session.Notice = result.Aborted
                ? "İşlem durduruldu; bazı öğeler taşınmadı."
                : $"Bazı öğeler taşınamadı (kod {result.ErrorCode}). Kullanımda olabilirler ya da yönetici izni gerekebilir.";
            Session.NoticeIsError = !result.Aborted;
        }
        return gone.Count > 0;
    }

    public static async Task ExportCsvAsync(int startNode, bool wholeTree)
    {
        if (Session.Index is not { } index || App.MainWindow is not { } window) return;
        var picker = new FileSavePicker(window.AppWindow.Id)
        {
            SuggestedFileName = $"Boyut - {SafeName(index.GetName(startNode))} - {DateTime.Now:yyyy-MM-dd}",
            CommitButtonText = "Dışa aktar",
        };
        picker.FileTypeChoices.Add("CSV (Excel)", [".csv"]);
        var result = await picker.PickSaveFileAsync();
        if (result is null) return;

        await Task.Run(() => CsvExporter.ExportToFile(result.Path, w =>
            CsvExporter.ExportTree(index, w, startNode, wholeTree ? int.MaxValue : 1, culture: CultureInfo.GetCultureInfo("tr-TR"))));
        Session.Notice = $"CSV kaydedildi: {result.Path}";
        Session.NoticeIsError = false;
    }

    public static async Task ExportNodesCsvAsync(IReadOnlyList<int> nodes, string name)
    {
        if (Session.Index is not { } index || App.MainWindow is not { } window || nodes.Count == 0) return;
        var picker = new FileSavePicker(window.AppWindow.Id) { SuggestedFileName = $"Boyut - {SafeName(name)}", CommitButtonText = "Dışa aktar" };
        picker.FileTypeChoices.Add("CSV (Excel)", [".csv"]);
        var result = await picker.PickSaveFileAsync();
        if (result is null) return;
        await Task.Run(() => CsvExporter.ExportToFile(result.Path, w =>
            CsvExporter.ExportNodes(index, w, nodes, CultureInfo.GetCultureInfo("tr-TR"))));
        Session.Notice = $"CSV kaydedildi: {result.Path}";
        Session.NoticeIsError = false;
    }

    public static async Task SaveSnapshotAsync()
    {
        if (Session.Index is not { } index || App.MainWindow is not { } window) return;
        var picker = new FileSavePicker(window.AppWindow.Id)
        {
            SuggestedFileName = $"{SafeName(index.GetName(FileIndex.RootNode))} {DateTime.Now:yyyy-MM-dd HHmm}",
            CommitButtonText = "Kaydet",
        };
        picker.FileTypeChoices.Add("Boyut taraması", [SnapshotSerializer.FileExtension]);
        var result = await picker.PickSaveFileAsync();
        if (result is null) return;
        await Task.Run(() => SnapshotSerializer.Save(index, result.Path));
        Session.Notice = $"Tarama kaydedildi: {result.Path}";
        Session.NoticeIsError = false;
    }

    /// <summary>Dialogs live in the popup layer and do not inherit the window's theme by themselves.</summary>
    public static ContentDialog Themed(ContentDialog dialog)
    {
        if (App.MainWindow?.Content is FrameworkElement root) dialog.RequestedTheme = root.ActualTheme;
        return dialog;
    }

    private static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "Boyut recycle" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('_', ' ');
        return cleaned.Length == 0 ? "Tarama" : cleaned;
    }

    private static void Fail(Exception ex)
    {
        Session.Notice = ex.Message;
        Session.NoticeIsError = true;
    }
}
