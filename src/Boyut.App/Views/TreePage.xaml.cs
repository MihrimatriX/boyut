using Boyut.App.Controls;
using Boyut.App.Services;
using Boyut.App.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;

namespace Boyut.App.Views;

public sealed partial class TreePage : Page, IRefreshable
{
    private const double MinDetailsWidth = 340;
    private const double MinTreeWidth = 420;
    private TreeRow? menuRow;
    private ResultRow? menuFile;
    private double dragStartWidth;

    public TreePage()
    {
        InitializeComponent();
        ViewModel.RowRevealed += (_, row) => TreeList.ScrollIntoView(row);
        DetailsPane.Width = Math.Clamp(SettingsStore.Current.TreeDetailsWidth, MinDetailsWidth, 1600);
        DetailsTabs.SelectedItem = DetailsTabs.Items[ViewModel.DetailsTab];
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

    // ---- details pane -----------------------------------------------------------------------

    private void OnDetailsTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        int tab = sender.Items.IndexOf(sender.SelectedItem);
        if (tab >= 0) ViewModel.DetailsTab = tab;
    }

    private void OnTileInvoked(object? sender, TreemapTile tile) => ViewModel.OpenTile(tile.Node);

    private void OnContentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int node }) ViewModel.OpenTile(node);
    }

    /// <summary>The tree keeps at least <see cref="MinTreeWidth"/>; the pane takes what the user dragged it to, within that.</summary>
    private double ClampDetails(double width)
    {
        double max = Math.Max(MinDetailsWidth, Body.ActualWidth - MinTreeWidth - Splitter.ActualWidth);
        return Math.Clamp(width, MinDetailsWidth, max);
    }

    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e) =>
        DetailsPane.Width = ClampDetails(SettingsStore.Current.TreeDetailsWidth);

    private void OnSplitterDragStarted(object? sender, EventArgs e) => dragStartWidth = DetailsPane.ActualWidth;

    // The pane is on the right: dragging the splitter left makes it wider.
    private void OnSplitterDragging(object? sender, double offset) => DetailsPane.Width = ClampDetails(dragStartWidth - offset);

    private void OnSplitterDragCompleted(object? sender, EventArgs e) => SaveDetailsWidth(DetailsPane.Width);

    private void OnSplitterReset(object? sender, EventArgs e)
    {
        DetailsPane.Width = ClampDetails(AppSettings.DefaultTreeDetailsWidth);
        SaveDetailsWidth(AppSettings.DefaultTreeDetailsWidth);
    }

    private static void SaveDetailsWidth(double width)
    {
        if (double.IsNaN(width) || width <= 0) return;
        SettingsStore.Current.TreeDetailsWidth = width;
        SettingsStore.Save();
    }

    // ---- tree -------------------------------------------------------------------------------

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedRow is { } row) ViewModel.Toggle(row);
    }

    private static bool IsCtrlDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

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
            case VirtualKey.Back:
                ViewModel.GoUpCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                ViewModel.Toggle(row);
                e.Handled = true;
                break;
            case VirtualKey.C when IsCtrlDown():
                ViewModel.CopyPathCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                e.Handled = true;
                if (!row.IsMore && row.Node > 0) await FileActions.RecycleAsync([row.Node], XamlRoot);
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
    private void OnRescanHereClick(object sender, RoutedEventArgs e) => ViewModel.RescanSelectedCommand.Execute(null);

    private async void OnRecycleClick(object sender, RoutedEventArgs e)
    {
        if (menuRow is { IsMore: false } row && row.Node > 0) await FileActions.RecycleAsync([row.Node], XamlRoot);
    }

    private async void OnRecycleSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedNode > 0) await FileActions.RecycleAsync([ViewModel.SelectedNode], XamlRoot);
    }

    // ---- largest files tab ------------------------------------------------------------------

    private void OnLargestDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (LargestList.SelectedItem is ResultRow row) ViewModel.ShowInTree(row);
    }

    private void OnLargestMenuOpening(object? sender, object e)
    {
        menuFile = (sender as MenuFlyout)?.Target?.DataContext as ResultRow;
        if (menuFile is not null) LargestList.SelectedItem = menuFile;
    }

    private void OnLargestShowInTreeClick(object sender, RoutedEventArgs e)
    {
        if (menuFile is { } row) ViewModel.ShowInTree(row);
    }

    private void OnLargestRevealClick(object sender, RoutedEventArgs e)
    {
        if (menuFile is { } row) FileActions.Reveal(row.FullPath);
    }

    private void OnLargestCopyClick(object sender, RoutedEventArgs e)
    {
        if (menuFile is { } row) FileActions.CopyText(row.FullPath);
    }

    private async void OnLargestRecycleClick(object sender, RoutedEventArgs e)
    {
        if (menuFile is { } row) await FileActions.RecyclePathsAsync([row.FullPath], XamlRoot);
    }
}
