using System.Text.Json;
using System.Text.Json.Serialization;
using Boyut.Core.Persistence;

namespace Boyut.App.Services;

public enum AppTheme { System, Light, Dark }

public sealed class SavedSearch
{
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public string Mode { get; set; } = "Glob";
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool UseMft { get; set; } = true;
    public bool IncludeHidden { get; set; } = true;
    public bool FollowLinks { get; set; }
    public bool IncludeStreams { get; set; }
    public bool ShowAdminTip { get; set; } = true;

    // Background monitor (scheduled task running "Boyut.exe --monitor"). Read by the task on every run.
    public bool MonitorEnabled { get; set; }
    public bool MonitorHourly { get; set; }
    /// <summary>Empty = the Windows drive.</summary>
    public List<string> MonitorRoots { get; set; } = [];
    public int MonitorFreeGb { get; set; } = 20;
    public int MonitorGrowthGb { get; set; } = 5;
    public List<SavedSearch> SavedSearches { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Settings and recent scans live under %LOCALAPPDATA%\Boyut (unpackaged apps cannot use ApplicationData).
/// </summary>
public static class SettingsStore
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Boyut");
    public static string ScansFolder => SnapshotHistory.DefaultFolder;
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; private set; } = Load();

    /// <summary>Every saved scan, per root. "Recent scans" is simply the newest entry of each root.</summary>
    public static SnapshotHistory History { get; } = new(ScansFolder);

    /// <summary>Before history existed each root had one file directly in the scans folder; move those in. Background thread.</summary>
    public static void MigrateLegacySnapshots()
    {
        if (!Directory.Exists(ScansFolder)) return;
        foreach (string file in Directory.EnumerateFiles(ScansFolder, "*" + SnapshotSerializer.FileExtension))
        {
            try { History.Import(file); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException) { }
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings) ?? Defaults();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return Defaults();
    }

    private static AppSettings Defaults() => new()
    {
        SavedSearches =
        [
            new() { Name = "Büyük videolar", Query = "type:video size:>500mb" },
            new() { Name = "Disk imajları ve arşivler", Query = "*.iso;*.img;*.vhdx;*.zip;*.7z;*.rar size:>100mb" },
            new() { Name = "Proje artıkları", Query = "kind:dir node_modules;Library;DerivedDataCache;Intermediate size:>50mb" },
            new() { Name = "Telefon fotoğrafları", Query = @"^IMG_\d{4}\.(jpg|jpeg|heic)$", Mode = "Regex" },
            new() { Name = "Kopya olabilecekler", Query = @"(\(\d+\)| - Kopya| - Copy)\.\w+$", Mode = "Regex" },
            new() { Name = "Bir yıldır dokunulmamış büyükler", Query = "kind:file size:>1gb age:>1y" },
            new() { Name = "Gizli büyük dosyalar", Query = "attr:hidden kind:file size:>100mb" },
            new() { Name = "Log ve döküm dosyaları", Query = "*.log;*.dmp;*.etl size:>10mb" },
        ],
    };

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
