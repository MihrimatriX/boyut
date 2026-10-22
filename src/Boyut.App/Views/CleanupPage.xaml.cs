using Boyut.App.Services;
using Boyut.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Boyut.App.Views;

public sealed partial class CleanupPage : Page
{
    public CleanupPage()
    {
        InitializeComponent();
    }

    public CleanupViewModel ViewModel => CleanupViewModel.Current;

    private async void OnCleanClick(object sender, RoutedEventArgs e) => await ViewModel.CleanSelectedAsync(XamlRoot);

    private void OnLargestClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ResultRow row) ViewModel.Inspect(row.Node);
    }

    private async void OnRemoveDuplicatesClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DuplicateRow row })
            await FileActions.RecycleAsync(row.Extra, XamlRoot);
    }
}
