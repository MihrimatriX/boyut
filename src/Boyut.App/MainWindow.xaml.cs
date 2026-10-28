using System.Runtime.InteropServices;
using Boyut.App.Services;
using Boyut.App.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics;

namespace Boyut.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Boyut.ico"));
        SizeAndCenter(1360, 880);
        ApplyTheme(SettingsStore.Current.Theme);
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            if (SettingsStore.Current.Theme == AppTheme.System) ApplyTheme(AppTheme.System);
        };

        Nav.SelectedItem = Nav.MenuItems[0];
    }

    public ScanSession Session => ScanSession.Current;

    public nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public XamlRoot? Root => Content?.XamlRoot;

    // ---- navigation -----------------------------------------------------------------------

    public void Navigate(string tag)
    {
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if ((string)item.Tag == tag)
            {
                if (ReferenceEquals(Nav.SelectedItem, item)) NavigateTo(tag); // already selected: refresh the page
                else Nav.SelectedItem = item;
                return;
            }
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        Nav.IsPaneOpen = false; // the open pane overlays the page
        if (args.IsSettingsSelected) { ContentFrame.Navigate(typeof(SettingsPage)); return; }
        if (args.SelectedItem is NavigationViewItem { Tag: string tag }) NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        var page = tag switch
        {
            "tree" => typeof(TreePage),
            "changes" => typeof(ChangesPage),
            "search" => typeof(SearchPage),
            "cleanup" => typeof(CleanupPage),
            _ => typeof(HomePage),
        };
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
        else if (ContentFrame.Content is IRefreshable refreshable) refreshable.Refresh();
    }

    // ---- actions used by pages ---------------------------------------------------------------

    public void StartScan(string path) => _ = ScanAndShowAsync(path);

    private async Task ScanAndShowAsync(string path)
    {
        if (await Session.ScanAsync(path)) Navigate("tree");
    }

    /// <param name="refresh">Recent scans: catch up with the change journal right after opening.</param>
    public async void OpenSnapshot(string file, bool refresh = false)
    {
        if (!await Session.OpenSnapshotAsync(file)) return;
        Navigate("tree");
        if (refresh) await Session.RefreshAsync(fallbackToScan: false);
    }

    public void Refresh() => _ = Session.RefreshAsync();

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(AppWindow.Id)
        {
            CommitButtonText = "Bu klasörü analiz et",
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }

    public void ApplyTheme(AppTheme theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        bool dark = theme == AppTheme.Dark ||
                    (theme == AppTheme.System && RootGrid.ActualTheme == ElementTheme.Dark);
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x80, 0, 0, 0);
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x10, 0, 0, 0);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x08, 0, 0, 0);
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
    }

    // ---- event handlers ----------------------------------------------------------------------

    public InfoBarSeverity NoticeSeverity(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args) => Session.Notice = null;

    private void OnPaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void OnCancelScan(object sender, RoutedEventArgs e) => Session.Cancel();

    private void OnFocusSearch(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!Session.HasIndex) return;
        GlobalSearch.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void OnRescan(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Session.HasIndex && !Session.IsScanning) Refresh();
        args.Handled = true;
    }

    private async void OnOpenFolder(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Session.IsScanning) return;
        if (await PickFolderAsync() is { } path) StartScan(path);
    }

    private void OnGlobalSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.QueryText)) return;
        Session.PendingSearch = (null, args.QueryText);
        Navigate("search");
    }

    // ---- window placement --------------------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private void SizeAndCenter(int width, int height)
    {
        double scale = GetDpiForWindow(Hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(width * scale), area.Width);
        int h = Math.Min((int)(height * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }
}

/// <summary>Pages that can redraw when re-selected in the navigation pane.</summary>
public interface IRefreshable
{
    void Refresh();
}
