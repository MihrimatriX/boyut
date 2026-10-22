using Boyut.App.Controls;
using Boyut.App.Services;
using Boyut.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Boyut.App.Views;

public sealed partial class TreePage : Page, IRefreshable
{
    private TreeRow? menuRow;

    public TreePage()
    {
        InitializeComponent();
        ViewModel.RowRevealed += (_, row) => TreeList.ScrollIntoView(row);
    }

    public TreeViewModel ViewModel => TreeViewModel.Current;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.ApplyPendingReveal();
    }

    public void Refresh() => ViewModel.ApplyPendingReveal();

    private void OnGoHome(object sender, RoutedEventArgs e) => App.MainWindow?.Navigate("home");

    private void OnCrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is Crumb crumb) ViewModel.OpenCrumb(crumb);
    }

    private void OnTileInvoked(object? sender, TreemapTile tile) => ViewModel.OpenTile(tile.Node);

    private void OnBarClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int node }) ViewModel.OpenTile(node);
    }

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedRow is { } row) ViewModel.Toggle(row);
    }

    private async void OnTreeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel.SelectedRow is not { } row) return;
        switch (e.Key)
        {
            case VirtualKey.Right:
                if (row.HasChildren && !row.IsExpanded) { ViewModel.Expand(row); e.Handled = true; }
                break;
            case VirtualKey.Left:
                if (row.IsExpanded) ViewModel.Collapse(row);
                else ViewModel.GoUpCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                ViewModel.Toggle(row);
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                e.Handled = true;
                if (!row.IsMore) await FileActions.RecycleAsync([row.Node], XamlRoot);
                break;
        }
    }

    // ---- row context menu: act on the row that was right-clicked -----------------------------

    private void OnRowMenuOpening(object? sender, object e)
    {
        menuRow = (sender as MenuFlyout)?.Target?.DataContext as TreeRow;
        if (menuRow is { IsMore: false }) ViewModel.SelectedRow = menuRow;
    }

    private void OnRevealClick(object sender, RoutedEventArgs e) => ViewModel.RevealInExplorerCommand.Execute(null);
    private void OnOpenClick(object sender, RoutedEventArgs e) => ViewModel.OpenItemCommand.Execute(null);
    private void OnCopyPathClick(object sender, RoutedEventArgs e) => ViewModel.CopyPathCommand.Execute(null);
    private void OnTerminalClick(object sender, RoutedEventArgs e) => ViewModel.OpenTerminalCommand.Execute(null);
    private void OnSearchHereClick(object sender, RoutedEventArgs e) => ViewModel.SearchHereCommand.Execute(null);

    private async void OnRecycleClick(object sender, RoutedEventArgs e)
    {
        if (menuRow is { IsMore: false } row) await FileActions.RecycleAsync([row.Node], XamlRoot);
    }

    private async void OnRecycleSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedNode > 0) await FileActions.RecycleAsync([ViewModel.SelectedNode], XamlRoot);
    }
}
