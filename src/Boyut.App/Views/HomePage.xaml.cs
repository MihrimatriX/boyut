using Boyut.App.ViewModels;
using Boyut.Core.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace Boyut.App.Views;

public sealed partial class HomePage : Page, IRefreshable
{
    public HomePage()
    {
        InitializeComponent();
    }

    public HomeViewModel ViewModel => HomeViewModel.Current;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    public void Refresh() => ViewModel.Refresh();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = "Boyut ile analiz et";
        DropOutline.StrokeThickness = 3;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOutline.StrokeThickness = 1;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOutline.StrokeThickness = 1;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || App.MainWindow is not { } window) return;
        var items = await e.DataView.GetStorageItemsAsync();

        if (items.OfType<StorageFolder>().FirstOrDefault() is { } folder)
        {
            window.StartScan(folder.Path);
            return;
        }
        if (items.OfType<StorageFile>().FirstOrDefault(f => f.Path.EndsWith(SnapshotSerializer.FileExtension, StringComparison.OrdinalIgnoreCase)) is { } snapshot)
            window.OpenSnapshot(snapshot.Path);
    }

    private void OnPathKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        ViewModel.ScanPathCommand.Execute(null);
        e.Handled = true;
    }
}
