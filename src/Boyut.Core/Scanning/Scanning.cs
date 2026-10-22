using Boyut.Core.Model;

namespace Boyut.Core.Scanning;

public sealed record ScanOptions
{
    /// <summary>Include hidden and system files (pagefile.sys, $Recycle.Bin …). On by default: they use real space.</summary>
    public bool IncludeHidden { get; init; } = true;

    /// <summary>Descend into junctions and directory symlinks. Off by default to avoid counting data twice.</summary>
    public bool FollowReparsePoints { get; init; }

    /// <summary>MFT engine only: add alternate data streams (ADS) to file sizes.</summary>
    public bool IncludeAlternateStreams { get; init; }

    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 16);

    /// <summary>Packs the options for the elevated helper's command line.</summary>
    public int ToBits() =>
        (IncludeHidden ? 1 : 0) | (FollowReparsePoints ? 2 : 0) | (IncludeAlternateStreams ? 4 : 0);

    public static ScanOptions FromBits(int bits) => new()
    {
        IncludeHidden = (bits & 1) != 0,
        FollowReparsePoints = (bits & 2) != 0,
        IncludeAlternateStreams = (bits & 4) != 0,
    };
}

/// <summary>Progress snapshot. <see cref="Fraction"/> is known only for the MFT engine.</summary>
public readonly record struct ScanProgress(long Items, long Bytes, string? CurrentPath, double? Fraction, string? Phase = null);

public interface IScanner
{
    /// <summary>"mft" or "managed".</summary>
    string Engine { get; }

    Task<FileIndex> ScanAsync(string rootPath, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a logical size into bytes-on-disk. The default rounds up to the cluster size;
/// the Windows implementation asks NTFS for compressed/sparse/cloud files.
/// </summary>
public interface IAllocationSizeProvider
{
    /// <summary>Cluster size used for rounding, 0 when unknown.</summary>
    int ClusterSize { get; }

    long GetAllocatedSize(ReadOnlySpan<char> fullPath, long logicalSize, int attributes);
}

public sealed class ClusterRoundingAllocation(int clusterSize = 4096) : IAllocationSizeProvider
{
    public int ClusterSize { get; } = clusterSize <= 0 ? 4096 : clusterSize;

    public long GetAllocatedSize(ReadOnlySpan<char> fullPath, long logicalSize, int attributes)
    {
        if (logicalSize <= 0) return 0;
        long c = ClusterSize;
        return (logicalSize + c - 1) / c * c;
    }
}
