using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Boyut.Core.Model;

namespace Boyut.Core.Scanning;

/// <summary>
/// Portable scanner: parallel, one directory per work item, built on
/// <see cref="FileSystemEnumerator{TResult}"/> (NtQueryDirectoryFile with a large buffer on
/// Windows, readdir on Unix). Works without admin rights and on any file system;
/// roughly 5–20× slower than reading the NTFS MFT directly.
/// </summary>
public sealed class ManagedScanner(IAllocationSizeProvider? allocation = null) : IScanner
{
    private readonly IAllocationSizeProvider allocation = allocation ?? new ClusterRoundingAllocation();

    public string Engine => "managed";

    public Task<FileIndex> ScanAsync(string rootPath, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() => Scan(rootPath, options, progress, cancellationToken),
            cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static string NormalizeRoot(string path)
    {
        if (path.Length == 2 && path[1] == ':') path += Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(full);
    }

    private FileIndex Scan(string rootPath, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string root = NormalizeRoot(rootPath);
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists) throw new DirectoryNotFoundException(root);

        var builder = new FileIndexBuilder(1 << 16);
        int rootNode = builder.AddRoot(root, SafeTicks(rootInfo), (int)rootInfo.Attributes);

        var context = new ScanContext(builder, allocation, options, ct);
        context.Queue.Add(new DirectoryWork(rootNode, root));

        using var timer = progress is null ? null : new Timer(_ =>
            progress.Report(new ScanProgress(
                Interlocked.Read(ref context.Items), Interlocked.Read(ref context.Bytes),
                context.CurrentPath, null, "Klasörler okunuyor")),
            null, 150, 150);

        int workers = Math.Max(1, options.MaxDegreeOfParallelism);
        var threads = new Task[workers];
        for (int i = 0; i < workers; i++)
            threads[i] = Task.Factory.StartNew(() => WorkerLoop(context, workers), ct,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            Task.WaitAll(threads, ct);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(ct);
        }
        ct.ThrowIfCancellationRequested();

        progress?.Report(new ScanProgress(context.Items, context.Bytes, null, 1, "İndeks hazırlanıyor"));

        var meta = new ScanMetadata
        {
            RootPath = root,
            Separator = Path.DirectorySeparatorChar,
            Engine = Engine,
            ScannedAtUtc = DateTime.UtcNow,
            Duration = stopwatch.Elapsed,
            ClusterSize = allocation.ClusterSize,
            ErrorCount = context.Errors,
        };
        meta = AddVolumeInfo(meta, root);

        var index = builder.Build(meta);
        return index;
    }

    internal static ScanMetadata AddVolumeInfo(ScanMetadata meta, string root)
    {
        try
        {
            string? volumeRoot = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(volumeRoot)) return meta;
            var drive = new DriveInfo(volumeRoot);
            if (!drive.IsReady) return meta;
            string? label = null;
            try { label = drive.VolumeLabel; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return meta with
            {
                VolumeLabel = label,
                FileSystem = drive.DriveFormat,
                VolumeTotalBytes = drive.TotalSize,
                VolumeFreeBytes = drive.AvailableFreeSpace,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return meta;
        }
    }

    private static long SafeTicks(FileSystemInfo info)
    {
        try { return info.LastWriteTimeUtc.Ticks; } catch (IOException) { return 0; }
    }

    private static void WorkerLoop(ScanContext context, int workers)
    {
        var local = new WorkerState();
        while (context.Queue.TryTake(out var work, context.Cancellation))
        {
            try
            {
                ProcessDirectory(context, local, work);
            }
            finally
            {
                context.Queue.Complete(workers);
            }
        }
    }

    private static void ProcessDirectory(ScanContext context, WorkerState local, DirectoryWork work)
    {
        local.Reset(work.Path);
        context.CurrentPath = work.Path;

        bool hadError;
        using (var enumerator = new DirectoryEnumerator(work.Path, context.EnumerationOptions, local, context))
        {
            try
            {
                while (enumerator.MoveNext()) { }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                enumerator.HadError = true;
            }
            hadError = enumerator.HadError;
        }

        if (hadError)
        {
            Interlocked.Increment(ref context.Errors);
            context.Builder.AddFlags(work.Node, NodeFlags.AccessDenied);
        }

        var entries = CollectionsMarshal.AsSpan(local.Entries);
        if (entries.IsEmpty) return;

        int first = context.Builder.AddBatch(work.Node, entries, local.Names.AsSpan(0, local.NamesLength));
        Interlocked.Add(ref context.Items, entries.Length);
        Interlocked.Add(ref context.Bytes, local.Bytes);

        foreach (var (offset, path) in local.SubDirectories)
            context.Queue.Add(new DirectoryWork(first + offset, path));
    }

    private readonly record struct DirectoryWork(int Node, string Path);

    private sealed class ScanContext
    {
        public ScanContext(FileIndexBuilder builder, IAllocationSizeProvider allocation, ScanOptions options, CancellationToken ct)
        {
            Builder = builder;
            Allocation = allocation;
            Options = options;
            Cancellation = ct;
            EnumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false, // we want ContinueOnError to see access errors
                ReturnSpecialDirectories = false,
                AttributesToSkip = options.IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
                BufferSize = 64 * 1024,
            };
        }

        public readonly FileIndexBuilder Builder;
        public readonly IAllocationSizeProvider Allocation;
        public readonly ScanOptions Options;
        public readonly CancellationToken Cancellation;
        public readonly EnumerationOptions EnumerationOptions;
        public readonly WorkQueue Queue = new();
        public long Items;
        public long Bytes;
        public int Errors;
        public volatile string? CurrentPath;
    }

    private sealed class WorkerState
    {
        public readonly List<PendingEntry> Entries = new(256);
        public readonly List<(int Offset, string Path)> SubDirectories = new(32);
        public char[] Names = new char[8192];
        public int NamesLength;
        public long Bytes;
        public string DirectoryPath = "";
        public char[] PathBuffer = new char[1024];

        public void Reset(string directory)
        {
            Entries.Clear();
            SubDirectories.Clear();
            NamesLength = 0;
            Bytes = 0;
            DirectoryPath = directory;
        }

        public int AppendName(ReadOnlySpan<char> name)
        {
            if (NamesLength + name.Length > Names.Length)
                Array.Resize(ref Names, Math.Max(Names.Length * 2, NamesLength + name.Length));
            int offset = NamesLength;
            name.CopyTo(Names.AsSpan(offset));
            NamesLength += name.Length;
            return offset;
        }

        /// <summary>Directory + separator + name in a reusable buffer, without allocating.</summary>
        public ReadOnlySpan<char> ComposePath(ReadOnlySpan<char> name)
        {
            int length = DirectoryPath.Length + 1 + name.Length;
            if (length > PathBuffer.Length) PathBuffer = new char[Math.Max(length, PathBuffer.Length * 2)];
            DirectoryPath.AsSpan().CopyTo(PathBuffer);
            int pos = DirectoryPath.Length;
            if (pos == 0 || !Path.EndsInDirectorySeparator(DirectoryPath)) PathBuffer[pos++] = Path.DirectorySeparatorChar;
            name.CopyTo(PathBuffer.AsSpan(pos));
            return PathBuffer.AsSpan(0, pos + name.Length);
        }
    }

    private sealed class DirectoryEnumerator : FileSystemEnumerator<int>
    {
        private readonly WorkerState state;
        private readonly ScanContext context;

        public DirectoryEnumerator(string directory, EnumerationOptions options, WorkerState state, ScanContext context)
            : base(directory, options)
        {
            this.state = state;
            this.context = context;
        }

        // Written from the base constructor too (ContinueOnError runs when the directory cannot be opened).
        public bool HadError { get; set; }

        protected override bool ContinueOnError(int error)
        {
            HadError = true;
            return true;
        }

        protected override int TransformEntry(ref FileSystemEntry entry)
        {
            var name = entry.FileName;
            int attributes = (int)entry.Attributes;
            bool isDirectory = entry.IsDirectory;
            bool reparse = (attributes & (int)FileAttributes.ReparsePoint) != 0;

            var flags = isDirectory ? NodeFlags.Directory : NodeFlags.None;
            if (reparse) flags |= NodeFlags.ReparsePoint;

            long length = 0, allocated = 0;
            if (!isDirectory)
            {
                length = entry.Length;
                if (ExtraFileAttributes.IsCloudPlaceholder(attributes)) flags |= NodeFlags.CloudPlaceholder;
                allocated = context.Allocation.GetAllocatedSize(state.ComposePath(name), length, attributes);
                state.Bytes += length;
            }

            long modified;
            try { modified = entry.LastWriteTimeUtc.UtcTicks; }
            catch (ArgumentOutOfRangeException) { modified = 0; }

            int offset = state.Entries.Count;
            state.Entries.Add(new PendingEntry
            {
                NameOffset = state.AppendName(name),
                NameLength = name.Length,
                Size = length,
                Allocated = allocated,
                ModifiedTicks = modified,
                Attributes = attributes,
                Flags = flags,
            });

            if (isDirectory && (!reparse || context.Options.FollowReparsePoints))
                state.SubDirectories.Add((offset, entry.ToFullPath()));

            return 0;
        }
    }

    /// <summary>Unbounded multi-producer/multi-consumer queue that knows when all work is done.</summary>
    private sealed class WorkQueue
    {
        private readonly ConcurrentQueue<DirectoryWork> queue = new();
        private readonly SemaphoreSlim available = new(0);
        private int outstanding;
        private volatile bool completed;

        public void Add(DirectoryWork work)
        {
            Interlocked.Increment(ref outstanding);
            queue.Enqueue(work);
            available.Release();
        }

        public bool TryTake(out DirectoryWork work, CancellationToken ct)
        {
            while (true)
            {
                available.Wait(ct);
                if (completed) { work = default; return false; }
                if (queue.TryDequeue(out work)) return true;
            }
        }

        /// <summary>Called once per finished item; wakes every worker when nothing is left.</summary>
        public void Complete(int workers)
        {
            if (Interlocked.Decrement(ref outstanding) == 0)
            {
                completed = true;
                available.Release(workers);
            }
        }
    }
}
