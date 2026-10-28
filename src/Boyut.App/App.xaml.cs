using Boyut.App.Services;
using Boyut.Core.Persistence;
using Microsoft.UI.Xaml;

namespace Boyut.App;

public partial class App : Application
{
    private readonly string[] arguments;

    public App(string[] arguments)
    {
        this.arguments = arguments;
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static MainWindow? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();

        // "Boyut.exe D:\Projeler" (e.g. from an Explorer context-menu entry) or "Boyut.exe tarama.boyut".
        string? target = arguments.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (target is null) return;
        // Explorer passes drives as "C:\" and the closing quote then arrives as C:" — repair it.
        target = target.Trim().TrimEnd('"');
        // "boyut:changes?root=C%3A%5C" from a background-monitor toast.
        if (BackgroundMonitor.ParseChangesUri(target) is { } changesRoot)
        {
            ScanSession.Current.PendingChangesRoot = changesRoot.Length > 0 ? changesRoot : null;
            MainWindow.Navigate("changes");
            return;
        }
        if (target.Length == 2 && target[1] == ':') target += "\\";
        if (target.EndsWith(SnapshotSerializer.FileExtension, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
            MainWindow.OpenSnapshot(target);
        else if (Directory.Exists(target))
            MainWindow.StartScan(target);
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Keep the app alive and tell the user; also leave a trace for bug reports.
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.AppendAllText(Path.Combine(SettingsStore.Folder, "hata.log"),
                $"{DateTime.Now:O}  {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }

        ScanSession.Current.Notice = "Beklenmeyen bir hata oluştu: " + e.Message;
        ScanSession.Current.NoticeIsError = true;
        e.Handled = true;
    }
}
