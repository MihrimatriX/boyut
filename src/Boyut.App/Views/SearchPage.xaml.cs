using Boyut.App.Services;
using Boyut.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Boyut.App.Views;

public sealed partial class SearchPage : Page, IRefreshable
{
    public SearchPage()
    {
        InitializeComponent();
    }

    public SearchViewModel ViewModel => SearchViewModel.Current;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh();
    }

    public void Refresh()
    {
        ViewModel.ApplyPending();
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectionStart = QueryBox.Text.Length;
    }

    private List<ResultRow> Selected() => ResultList.SelectedItems.OfType<ResultRow>().ToList();

    private void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        int index = sender.Items.IndexOf(sender.SelectedItem);
        if (index >= 0) ViewModel.ModeIndex = index;
    }

    private void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.UpdateSelection(Selected());

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key }) ViewModel.SortBy(key);
    }

    private void OnSavedClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SavedSearchItem item) return;
        ViewModel.ApplySaved(item);
        ModeBar.SelectedItem = ModeBar.Items[ViewModel.ModeIndex];
    }

    private void OnDeleteSavedClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SavedSearchItem item }) ViewModel.DeleteSaved(item);
    }

    private void OnSyntaxClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SyntaxHint hint) ViewModel.InsertSyntax(hint);
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectionStart = QueryBox.Text.Length;
    }

    private async void OnSaveSearchClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.QueryText)) return;
        var name = new TextBox { PlaceholderText = "ör. Eski oyun kayıtları", Text = "" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Aramayı kaydet",
            Content = name,
            PrimaryButtonText = "Kaydet",
            CloseButtonText = "Vazgeç",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await FileActions.Themed(dialog).ShowAsync() == ContentDialogResult.Primary) ViewModel.SaveCurrent(name.Text);
    }

    private void OnResultDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ResultList.SelectedItem is ResultRow row) ViewModel.ShowInTree(row);
    }

    private async void OnResultKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Delete:
                e.Handled = true;
                await RecycleSelected();
                break;
            case VirtualKey.Enter when ResultList.SelectedItem is ResultRow row:
                e.Handled = true;
                ViewModel.ShowInTree(row);
                break;
        }
    }

    private void OnShowInTreeClick(object sender, RoutedEventArgs e)
    {
        if (Selected().FirstOrDefault() is { } row) ViewModel.ShowInTree(row);
    }

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        if (Selected().FirstOrDefault() is { } row) ViewModel.Reveal(row);
    }

    private void OnCopyPathsClick(object sender, RoutedEventArgs e) => ViewModel.CopyPaths(Selected());

    private async void OnRecycleClick(object sender, RoutedEventArgs e) => await RecycleSelected();

    private async Task RecycleSelected()
    {
        var rows = Selected();
        if (rows.Count > 0) await FileActions.RecyclePathsAsync(rows.Select(r => r.FullPath).ToList(), XamlRoot);
    }

    private async void OnExportClick(object sender, RoutedEventArgs e) => await ViewModel.ExportAsync();
}
