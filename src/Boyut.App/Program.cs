using Boyut.Platform.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Boyut.App;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // "Boyut.exe --scan-helper …" is the elevated MFT scanner started by the UI. No window, no XAML.
        if (ScanHelperHost.IsHelperInvocation(args))
            return ScanHelperHost.RunAsync(args).GetAwaiter().GetResult();

        // "Boyut.exe -Embedding" is COM starting the Windows 11 context-menu command. No window.
        if (Services.ContextMenuServer.IsComLaunch(args)) return Services.ContextMenuServer.Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // "Boyut.exe --monitor" is the scheduled background scan. No window either.
        if (args.Length > 0 && args[0] == Services.BackgroundMonitor.Verb)
            return Services.BackgroundMonitor.RunAsync().GetAwaiter().GetResult();

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(args);
        });
        return 0;
    }
}
