using Boyut.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Boyut.App.Views;

public sealed partial class ChangesPage : Page, IRefreshable
{
    public ChangesPage()
    {
        InitializeComponent();
    }

    public ChangesViewModel ViewModel => ChangesViewModel.Current;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    public void Refresh() => ViewModel.Refresh();

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedRow is { } row) ViewModel.Toggle(row);
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel.SelectedRow is not { } row) return;
        switch (e.Key)
        {
            case VirtualKey.Right: ViewModel.Expand(row); e.Handled = true; break;
            case VirtualKey.Left: ViewModel.Collapse(row); e.Handled = true; break;
            case VirtualKey.Enter: ViewModel.Toggle(row); e.Handled = true; break;
        }
    }
}
