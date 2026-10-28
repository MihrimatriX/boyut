using Boyut.App.Services;
using Boyut.Platform.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Boyut.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeButtons.SelectedIndex = (int)SettingsStore.Current.Theme;
        EngineStatus.Text = Elevation.IsElevated
            ? "Boyut şu an yönetici olarak çalışıyor: MFT taramaları izin sormadan başlar."
            : "Boyut normal kullanıcı olarak çalışıyor: MFT taraması başlarken Windows bir kez izin ister.";
        DataFolder.Text = $"Ayarlar ve kaydedilmiş taramalar: {SettingsStore.Folder}";
        VersionText.Text = $"Boyut {typeof(App).Assembly.GetName().Version?.ToString(3)} · .NET {Environment.Version} · Windows App SDK";

        var s = SettingsStore.Current;
        MonitorSwitch.IsOn = s.MonitorEnabled;
        MonitorFrequency.SelectedIndex = s.MonitorHourly ? 1 : 0;
        FreeGb.Value = s.MonitorFreeGb;
        GrowthGb.Value = s.MonitorGrowthGb;
        var roots = BackgroundMonitor.Roots(s);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            }
            catch (IOException) { continue; }
            var box = new CheckBox
            {
                Content = drive.Name.TrimEnd('\\'),
                Tag = drive.Name,
                IsChecked = roots.Any(r => string.Equals(r, drive.Name, StringComparison.OrdinalIgnoreCase)),
            };
            box.Click += OnMonitorDriveClicked;
            MonitorDrives.Children.Add(box);
        }
        _ = UpdateMonitorStatusAsync();
        loading = false;
    }

    // ---- background monitor -----------------------------------------------------------------

    private async Task UpdateMonitorStatusAsync(string? message = null)
    {
        bool registered = await BackgroundMonitor.IsRegisteredAsync();
        string state = message ?? (registered ? "Görev kayıtlı." : SettingsStore.Current.MonitorEnabled ? "Görev bulunamadı; kapatıp yeniden açın." : "Kapalı.");
        MonitorStatus.Text = BackgroundMonitor.LastRun() is { } last ? $"{state}  Son çalışma: {last}" : state;
        RunNowButton.IsEnabled = registered;
    }

    private async void OnMonitorToggled(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        MonitorSwitch.IsEnabled = false;
        try
        {
            string message;
            if (MonitorSwitch.IsOn) message = await BackgroundMonitor.EnableAsync(SettingsStore.Current.MonitorHourly);
            else { await BackgroundMonitor.DisableAsync(); message = "Kapalı."; }
            SettingsStore.Current.MonitorEnabled = MonitorSwitch.IsOn;
            SettingsStore.Save();
            await UpdateMonitorStatusAsync(message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            loading = true;
            MonitorSwitch.IsOn = !MonitorSwitch.IsOn;
            loading = false;
            MonitorStatus.Text = ex.Message;
        }
        finally
        {
            MonitorSwitch.IsEnabled = true;
        }
    }

    private async void OnMonitorFrequencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || MonitorFrequency.SelectedIndex < 0) return;
        SettingsStore.Current.MonitorHourly = MonitorFrequency.SelectedIndex == 1;
        SettingsStore.Save();
        // The schedule lives in the task, so it has to be registered again.
        if (SettingsStore.Current.MonitorEnabled)
            await UpdateMonitorStatusAsync(await BackgroundMonitor.EnableAsync(SettingsStore.Current.MonitorHourly));
    }

    private void OnMonitorDriveClicked(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.MonitorRoots = MonitorDrives.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        SettingsStore.Save();
    }

    private void OnThresholdChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (loading || double.IsNaN(args.NewValue)) return;
        SettingsStore.Current.MonitorFreeGb = (int)FreeGb.Value;
        SettingsStore.Current.MonitorGrowthGb = (int)GrowthGb.Value;
        SettingsStore.Save();
    }

    private async void OnMonitorRunNow(object sender, RoutedEventArgs e)
    {
        bool started = await BackgroundMonitor.RunNowAsync();
        MonitorStatus.Text = started
            ? "Tarama arka planda başladı; bitince Son taramalar ve Değişimler güncellenir."
            : "Görev başlatılamadı.";
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || ThemeButtons.SelectedIndex < 0) return;
        var theme = (AppTheme)ThemeButtons.SelectedIndex;
        SettingsStore.Current.Theme = theme;
        SettingsStore.Save();
        App.MainWindow?.ApplyTheme(theme);
    }

    private void OnRestartElevated(object sender, RoutedEventArgs e)
    {
        if (Elevation.RestartElevated()) Application.Current.Exit();
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.Folder);
        FileActions.Open(SettingsStore.Folder);
    }

    private void OnClearRecent(object sender, RoutedEventArgs e)
    {
        SettingsStore.History.ForgetAll();
        ViewModels.HomeViewModel.Current.Refresh();
    }
}
