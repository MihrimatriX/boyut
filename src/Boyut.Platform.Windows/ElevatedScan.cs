using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using Boyut.Core.Ipc;
using Boyut.Core.Model;
using Boyut.Core.Persistence;
using Boyut.Core.Scanning;

namespace Boyut.Platform.Windows;

/// <summary>
/// App side: runs the MFT scan in an elevated copy of the current executable and streams its
/// progress back over a named pipe. The UI process itself never needs admin rights.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevatedScanClient
{
    /// <exception cref="OperationCanceledException">The user declined UAC or cancelled the scan.</exception>
    public static async Task<FileIndex> ScanAsync(string root, ScanOptions options, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken, string? executable = null)
    {
        executable ??= Environment.ProcessPath ?? throw new InvalidOperationException("Uygulama yolu bulunamadı.");
        string pipeName = "boyut-scan-" + Guid.NewGuid().ToString("N");
        string output = Path.Combine(Path.GetTempPath(), $"boyut-{Guid.NewGuid():N}{SnapshotSerializer.FileExtension}");

        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var args = HelperProtocol.JoinForCommandLine(HelperProtocol.BuildArguments(root, output, pipeName, options));
        using var process = Elevation.StartElevated(executable, args)
            ?? throw new OperationCanceledException("Yönetici izni verilmedi.");

        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var exited = process.WaitForExitAsync(connectTimeout.Token);
            var connected = server.WaitForConnectionAsync(connectTimeout.Token);
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) != connected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException("Tarama yardımcısı başlatılamadı.");
            }
            await connected.ConfigureAwait(false);

            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), 256, leaveOpen: true) { AutoFlush = true };
            await using var cancelRegistration = cancellationToken.Register(() =>
            {
                try { writer.WriteLine(HelperProtocol.Cancel); } catch (IOException) { } catch (ObjectDisposedException) { }
            });

            while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                var (kind, p, error) = HelperProtocol.Parse(line);
                switch (kind)
                {
                    case HelperProtocol.MessageKind.Progress:
                        progress?.Report(p);
                        break;
                    case HelperProtocol.MessageKind.Error:
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new IOException(error);
                    case HelperProtocol.MessageKind.Done:
                        return await Task.Run(() => SnapshotSerializer.Load(output), CancellationToken.None).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("Tarama yardımcısı beklenmedik şekilde kapandı.");
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Helper side: entered when the executable is started with <see cref="HelperProtocol.Verb"/>.</summary>
public static class ScanHelperHost
{
    public static bool IsHelperInvocation(IReadOnlyList<string> args) => args.Count > 0 && args[0] == HelperProtocol.Verb;

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var parsed = HelperProtocol.TryParse(args);
        if (parsed is null) return 2;

        await using var pipe = new NamedPipeClientStream(".", parsed.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(15_000).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return 3;
        }

        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 256, leaveOpen: true);
        using var cts = new CancellationTokenSource();
        var gate = new SemaphoreSlim(1, 1);

        async Task Send(string line)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { await writer.WriteLineAsync(line).ConfigureAwait(false); }
            catch (IOException) { cts.Cancel(); }
            finally { gate.Release(); }
        }

        // Listen for "C" (cancel) from the app; a closed pipe also cancels.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync(cts.Token).ConfigureAwait(false) is { } line)
                    if (line == HelperProtocol.Cancel) break;
            }
            catch (Exception) { }
            cts.Cancel();
        });

        try
        {
            var progress = new SyncProgress(p => _ = Send(HelperProtocol.Progress(p)));
            var scanner = OperatingSystem.IsWindows() && ScannerFactory.IsLocalNtfs(parsed.Root)
                ? (IScanner)new MftScanner()
                : ScannerFactory.Create(ScanEngine.Managed, parsed.Root);
            var index = await scanner.ScanAsync(parsed.Root, parsed.Options, progress, cts.Token).ConfigureAwait(false);
            SnapshotSerializer.Save(index, parsed.OutputFile);
            await Send(HelperProtocol.Done).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            await Send(HelperProtocol.Error("İptal edildi.")).ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await Send(HelperProtocol.Error(ex.Message)).ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>IProgress that reports on the caller's thread (no SynchronizationContext in the helper).</summary>
    private sealed class SyncProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
