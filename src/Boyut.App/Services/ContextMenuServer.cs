using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Boyut.App.Services;

/// <summary>
/// "Boyut.exe -Embedding": the out-of-process COM server behind "Boyut ile analiz et" in Windows 11's new
/// context menu. The sparse package (tools/register-modern-context-menu.ps1) tells Explorer which CLSID to ask;
/// COM starts this exe, Explorer asks for a title and icon, and Invoke opens the app on the clicked folder.
/// </summary>
internal static partial class ContextMenuServer
{
    /// <summary>Must match the Clsid in tools/context-menu/AppxManifest.xml.</summary>
    public static readonly Guid Clsid = new("1B0D848D-B31B-40E6-AE87-45523F2C897F");

    private const uint CLSCTX_LOCAL_SERVER = 4, REGCLS_MULTIPLEUSE = 1, COINIT_MULTITHREADED = 0;
    private const int E_NOTIMPL = unchecked((int)0x80004001), CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private static readonly TimeSpan IdleExit = TimeSpan.FromSeconds(60);
    private static long lastCall = Environment.TickCount64;

    private static string Exe => Environment.ProcessPath!;

    public static bool IsComLaunch(string[] args) =>
        args.Any(a => a.Equals("-Embedding", StringComparison.OrdinalIgnoreCase) || a.Equals("/Embedding", StringComparison.OrdinalIgnoreCase));

    public static int Run()
    {
        int result = 0;
        // Main is [STAThread]; an MTA thread receives the calls on RPC threads without needing a message pump.
        var thread = new Thread(() =>
        {
            CoInitializeEx(0, COINIT_MULTITHREADED);
            var wrappers = new StrategyBasedComWrappers();
            nint factory = wrappers.GetOrCreateComInterfaceForObject(new Factory(wrappers), CreateComInterfaceFlags.None);
            result = CoRegisterClassObject(Clsid, factory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out uint cookie);
            if (result < 0) return;
            // ponytail: idle timer instead of reference counting; Explorer simply starts us again when needed.
            while (Environment.TickCount64 - Interlocked.Read(ref lastCall) < IdleExit.TotalMilliseconds) Thread.Sleep(1000);
            CoRevokeClassObject(cookie);
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return result < 0 ? 1 : 0;
    }

    private static void Touch() => Interlocked.Exchange(ref lastCall, Environment.TickCount64);

    [GeneratedComClass]
    private sealed partial class Factory(StrategyBasedComWrappers wrappers) : IClassFactory
    {
        public int CreateInstance(nint outer, in Guid riid, out nint obj)
        {
            Touch();
            obj = 0;
            if (outer != 0) return CLASS_E_NOAGGREGATION;
            nint unknown = wrappers.GetOrCreateComInterfaceForObject(new Command(wrappers), CreateComInterfaceFlags.None);
            try { return Marshal.QueryInterface(unknown, riid, out obj); }
            finally { Marshal.Release(unknown); }
        }

        public int LockServer(bool lockServer) { Touch(); return 0; }
    }

    [GeneratedComClass]
    private sealed partial class Command(StrategyBasedComWrappers wrappers) : IExplorerCommand
    {
        public int GetTitle(nint items, out nint name) { Touch(); name = Marshal.StringToCoTaskMemUni("Boyut ile analiz et"); return 0; }
        public int GetIcon(nint items, out nint icon) { icon = Marshal.StringToCoTaskMemUni($"{Exe},0"); return 0; }
        public int GetToolTip(nint items, out nint tip) { tip = 0; return E_NOTIMPL; }
        public int GetCanonicalName(out Guid guid) { guid = Clsid; return 0; }
        public int GetState(nint items, bool okToBeSlow, out uint state) { state = 0; return 0; } // ECS_ENABLED
        public int GetFlags(out uint flags) { flags = 0; return 0; }
        public int EnumSubCommands(out nint enumerator) { enumerator = 0; return E_NOTIMPL; }

        public int Invoke(nint items, nint bindContext)
        {
            Touch();
            if (items == 0) return 0;
            var array = (IShellItemArray)wrappers.GetOrCreateObjectForComInstance(items, CreateObjectFlags.None);
            if (array.GetCount(out uint count) < 0 || count == 0 || array.GetItemAt(0, out var item) < 0) return 0;
            const uint SIGDN_FILESYSPATH = 0x80058000;
            if (item.GetDisplayName(SIGDN_FILESYSPATH, out nint text) < 0) return 0;
            string path = Marshal.PtrToStringUni(text) ?? "";
            Marshal.FreeCoTaskMem(text);
            if (path.Length > 0) Process.Start(new ProcessStartInfo(Exe) { ArgumentList = { path }, UseShellExecute = false });
            return 0;
        }
    }

    [GeneratedComInterface, Guid("00000001-0000-0000-C000-000000000046")]
    internal partial interface IClassFactory
    {
        [PreserveSig] int CreateInstance(nint outer, in Guid riid, out nint obj);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
    }

    [GeneratedComInterface, Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9")]
    internal partial interface IExplorerCommand
    {
        [PreserveSig] int GetTitle(nint items, out nint name);
        [PreserveSig] int GetIcon(nint items, out nint icon);
        [PreserveSig] int GetToolTip(nint items, out nint tip);
        [PreserveSig] int GetCanonicalName(out Guid guid);
        [PreserveSig] int GetState(nint items, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out uint state);
        [PreserveSig] int Invoke(nint items, nint bindContext);
        [PreserveSig] int GetFlags(out uint flags);
        [PreserveSig] int EnumSubCommands(out nint enumerator);
    }

    [GeneratedComInterface, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    internal partial interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint obj);
        [PreserveSig] int GetPropertyStore(int flags, in Guid riid, out nint obj);
        [PreserveSig] int GetPropertyDescriptionList(nint keyType, in Guid riid, out nint obj);
        [PreserveSig] int GetAttributes(int flags, uint mask, out uint attributes);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemAt(uint index, out IShellItem item);
        [PreserveSig] int EnumItems(out nint enumerator);
    }

    [GeneratedComInterface, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    internal partial interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint obj);
        [PreserveSig] int GetParent(out nint parent);
        [PreserveSig] int GetDisplayName(uint form, out nint name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(nint other, uint hint, out int order);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(in Guid clsid, nint unknown, uint context, uint flags, out uint cookie);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint cookie);
}
