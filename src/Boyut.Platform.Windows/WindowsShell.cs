using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Boyut.Platform.Windows;

public static class LongPath
{
    /// <summary>Adds the \\?\ prefix so Win32 calls accept paths longer than 260 characters.</summary>
    public static string Of(ReadOnlySpan<char> path)
    {
        if (path.StartsWith(@"\\?\")) return path.ToString();
        if (path.StartsWith(@"\\")) return @"\\?\UNC\" + path[2..].ToString();
        return @"\\?\" + path.ToString();
    }
}

public static class Elevation
{
    private static readonly Lazy<bool> Elevated = new(() => OperatingSystem.IsWindows() && QueryElevated());

    /// <summary>True when the process runs with a full administrator token.</summary>
    public static bool IsElevated => Elevated.Value;

    [SupportedOSPlatform("windows")]
    private static bool QueryElevated()
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TOKEN_QUERY, out nint token)) return false;
        try
        {
            return NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, out int elevated, sizeof(int), out _) && elevated != 0;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    /// <summary>Starts a process through UAC. Returns null when the user says "No".</summary>
    [SupportedOSPlatform("windows")]
    public static Process? StartElevated(string fileName, string arguments)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == NativeMethods.ERROR_CANCELLED)
        {
            return null;
        }
    }

    /// <summary>Restarts the current executable as administrator. Returns false if the user cancelled.</summary>
    [SupportedOSPlatform("windows")]
    public static bool RestartElevated(string arguments = "")
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return false;
        var p = StartElevated(exe, arguments);
        if (p is null) return false;
        p.Dispose();
        return true;
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsShell
{
    public sealed record RecycleResult(bool Success, bool Aborted, int ErrorCode);

    /// <summary>
    /// Moves files and folders to the Recycle Bin (undoable). Windows itself warns before
    /// permanently deleting anything too large for the bin.
    /// </summary>
    public static RecycleResult SendToRecycleBin(IReadOnlyCollection<string> paths, nint ownerWindow = 0, bool showProgress = true)
    {
        if (paths.Count == 0) return new RecycleResult(true, false, 0);

        // pFrom: NUL-separated, double-NUL-terminated list of full paths (no \\?\ prefix: the shell rejects it).
        var sb = new StringBuilder();
        foreach (var p in paths) sb.Append(Path.GetFullPath(p)).Append('\0');
        sb.Append('\0');

        nint from = Marshal.StringToHGlobalUni(sb.ToString());
        try
        {
            ushort flags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_WANTNUKEWARNING;
            if (!showProgress) flags |= NativeMethods.FOF_SILENT;
            var op = new NativeMethods.SHFILEOPSTRUCTW
            {
                hwnd = ownerWindow,
                wFunc = NativeMethods.FO_DELETE,
                pFrom = from,
                fFlags = flags,
            };
            int result = NativeMethods.SHFileOperation(ref op);
            return new RecycleResult(result == 0 && op.fAnyOperationsAborted == 0, op.fAnyOperationsAborted != 0, result);
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    /// <summary>Empties the Recycle Bin of one drive ("C:\") or of all drives (null).</summary>
    public static bool EmptyRecycleBin(string? driveRoot = null, nint ownerWindow = 0) =>
        NativeMethods.SHEmptyRecycleBin(ownerWindow, driveRoot, 0) == 0;

    /// <summary>Opens Explorer with the item selected.</summary>
    public static void RevealInExplorer(string path)
    {
        if (Directory.Exists(path) && !File.Exists(path) && Path.GetPathRoot(path) == path)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>Opens a file or folder with its default handler.</summary>
    public static void Open(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Opens Windows Terminal (or cmd) in a folder.</summary>
    public static void OpenTerminal(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = folder });
        }
    }

    /// <summary>Volume serial + file id: equal for two names of one hard-linked file.</summary>
    public static (ulong Volume, ulong FileId)? GetFileIdentity(string path)
    {
        using var handle = NativeMethods.CreateFile(LongPath.Of(path), NativeMethods.FILE_READ_ATTRIBUTES,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            0, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (handle.IsInvalid) return null;
        if (!NativeMethods.GetFileInformationByHandle(handle, out var info)) return null;
        return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }
}
