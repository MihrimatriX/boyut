using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using Boyut.Core.Analysis;
using Boyut.Core.Formatting;
using Boyut.Core.Persistence;
using Boyut.Core.Scanning;
using Boyut.Platform.Windows;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace Boyut.App.Services;

/// <summary>
/// "Boyut.exe --monitor": started by Task Scheduler, scans the chosen drives without a window, adds the scans
/// to the history and raises a toast when space runs low or a drive grew a lot. Clicking the toast opens
/// <c>boyut:changes?root=…</c>, i.e. the Changes page of the app.
/// </summary>
public static class BackgroundMonitor
{
    public const string Verb = "--monitor";
    public const string TaskName = @"Boyut\Arka plan taraması";
    private const string Aumid = "Boyut.App";
    private const string Protocol = "boyut";

    private static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("Uygulama yolu bulunamadı.");
    private static string LogFile => Path.Combine(SettingsStore.Folder, "izleme.log");

    public static IReadOnlyList<string> Roots(AppSettings s) =>
        s.MonitorRoots.Count > 0 ? s.MonitorRoots : [Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\"];

    // ---- the scheduled run ------------------------------------------------------------------

    public static async Task<int> RunAsync()
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Win32Exception) { }
        // Self-healing: keeps the toast identity and boyut: link pointing at this exe even after it moved.
        try { RegisterShellIntegration(); } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException) { }
        var s = SettingsStore.Current;
        var thresholds = new AlertThresholds(s.MonitorFreeGb * (1L << 30), s.MonitorGrowthGb * (1L << 30));
        var options = new ScanOptions { IncludeHidden = s.IncludeHidden, FollowReparsePoints = s.FollowLinks, IncludeAlternateStreams = s.IncludeStreams };
        int failures = 0;

        foreach (string root in Roots(s))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var scanner = Elevation.IsElevated && ScannerFactory.IsLocalNtfs(root)
                    ? (IScanner)new MftScanner()
                    : ScannerFactory.Create(ScanEngine.Managed, root);
                var index = await scanner.ScanAsync(root, options);

                var previous = SettingsStore.History.List(root).FirstOrDefault();
                SettingsStore.History.Save(index);
                SettingsStore.History.Prune(root, DateTime.UtcNow);

                var diff = previous is null ? null : new SnapshotDiff(SnapshotSerializer.Load(previous.File), index);
                Log($"{root}  {SizeFormatter.Format(index.TotalSize)}  {(diff is null ? "ilk tarama" : (diff.TotalDelta >= 0 ? "+" : "") + SizeFormatter.Format(diff.TotalDelta))}  " +
                    $"boş {SizeFormatter.Format(index.Metadata.VolumeFreeBytes ?? 0)}  {index.Metadata.Engine}  {SizeFormatter.FormatDuration(index.Metadata.Duration)}");

                if (SpaceAlerts.Evaluate(index, diff, thresholds) is { } alert) ShowToast(alert, root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or EndOfStreamException)
            {
                failures++;
                Log($"{root}  HATA: {ex.Message}");
            }
        }
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Last line of the log, for the settings page ("Son çalışma: …").</summary>
    public static string? LastRun()
    {
        try { return File.Exists(LogFile) ? File.ReadLines(LogFile).LastOrDefault() : null; }
        catch (IOException) { return null; }
    }

    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            // ponytail: grows ~100 bytes per run (≈1 MB/year hourly); trim if it ever matters.
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm}  {line}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    // ---- toast + protocol -------------------------------------------------------------------

    public static string ChangesUri(string root) => $"{Protocol}:changes?root={Uri.EscapeDataString(root)}";

    /// <summary>Root from <c>boyut:changes?root=…</c>, or null when the argument is something else.</summary>
    public static string? ParseChangesUri(string arg)
    {
        if (!arg.StartsWith(Protocol + ":changes", StringComparison.OrdinalIgnoreCase)) return null;
        int q = arg.IndexOf("root=", StringComparison.OrdinalIgnoreCase);
        return q < 0 ? "" : Uri.UnescapeDataString(arg[(q + 5)..].Split('&')[0]);
    }

    private static void ShowToast(SpaceAlert alert, string root)
    {
        string uri = SecurityElement.Escape(ChangesUri(root));
        var xml = new Windows.Data.Xml.Dom.XmlDocument();
        xml.LoadXml($"""
            <toast launch="{uri}" activationType="protocol">
              <visual><binding template="ToastGeneric">
                <text>{SecurityElement.Escape(alert.Title)}</text>
                <text>{SecurityElement.Escape(alert.Message)}</text>
              </binding></visual>
              <actions><action content="Değişimleri göster" activationType="protocol" arguments="{uri}" /></actions>
            </toast>
            """);
        try { ToastNotificationManager.CreateToastNotifier(Aumid).Show(new ToastNotification(xml)); }
        catch (Exception ex) { Log("Bildirim gösterilemedi: " + ex.Message); }
    }

    /// <summary>HKCU only: the toast sender identity and the <c>boyut:</c> link that opens the app.</summary>
    public static void RegisterShellIntegration()
    {
        using (var app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}"))
        {
            app.SetValue("DisplayName", "Boyut");
            app.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "Boyut.png"));
        }
        using var proto = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Protocol}");
        proto.SetValue("", "URL:Boyut");
        proto.SetValue("URL Protocol", "");
        using (var icon = proto.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{Exe}\",0");
        using var command = proto.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{Exe}\" \"%1\"");
    }

    // ---- Task Scheduler ---------------------------------------------------------------------

    /// <summary>Registers the task; asks UAC once so the scans can use the MFT. Returns a sentence for the UI.</summary>
    public static async Task<string> EnableAsync(bool hourly)
    {
        RegisterShellIntegration();
        if (await CreateTaskAsync(hourly, highest: true))
            return "Arka plan taraması kuruldu: yönetici izniyle, hızlı MFT taramasıyla çalışacak.";
        if (await CreateTaskAsync(hourly, highest: false))
            return "Yönetici izni verilmedi; arka plan taraması klasör taramasıyla (daha yavaş) çalışacak.";
        throw new IOException("Görev Zamanlayıcı'ya kayıt yapılamadı.");
    }

    public static async Task DisableAsync()
    {
        string args = $"/Delete /TN \"{TaskName}\" /F";
        if (await SchtasksAsync(args, elevated: false) != 0) await SchtasksAsync(args, elevated: true);
    }

    public static async Task<bool> RunNowAsync() => await SchtasksAsync($"/Run /TN \"{TaskName}\"", elevated: false) == 0;

    public static async Task<bool> IsRegisteredAsync() => await SchtasksAsync($"/Query /TN \"{TaskName}\"", elevated: false) == 0;

    private static async Task<bool> CreateTaskAsync(bool hourly, bool highest)
    {
        string file = Path.Combine(Path.GetTempPath(), $"boyut-gorev-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(file, TaskXml(hourly, highest), Encoding.Unicode);
        try { return await SchtasksAsync($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", elevated: highest) == 0; }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    /// <returns>schtasks exit code; -1 when UAC was declined.</returns>
    private static async Task<int> SchtasksAsync(string args, bool elevated)
    {
        using var process = elevated
            ? Elevation.StartElevated("schtasks.exe", args)
            : Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            });
        if (process is null) return -1;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    internal static string TaskXml(bool hourly, bool highest)
    {
        var start = DateTime.Today.AddHours(hourly ? 0 : 12).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        string repetition = hourly ? "<Repetition><Interval>PT1H</Interval><Duration>P1D</Duration></Repetition>" : "";
        string user = SecurityElement.Escape($@"{Environment.UserDomainName}\{Environment.UserName}");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Boyut: sürücüleri düzenli tarar, tarama geçmişine ekler, yer azalınca bildirir.</Description></RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{start}</StartBoundary>
                  {repetition}
                  <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>{(highest ? "HighestAvailable" : "LeastPrivilege")}</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(Exe)}</Command><Arguments>{Verb}</Arguments></Exec>
              </Actions>
            </Task>
            """;
    }
}
