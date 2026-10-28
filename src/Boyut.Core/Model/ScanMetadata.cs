namespace Boyut.Core.Model;

/// <summary>Everything we know about how and where an index was produced.</summary>
public sealed record ScanMetadata
{
    public required string RootPath { get; init; }

    /// <summary>Path separator of the machine that produced the scan (snapshots can be opened anywhere).</summary>
    public char Separator { get; init; } = Path.DirectorySeparatorChar;

    /// <summary>"mft", "managed" or a future engine name.</summary>
    public required string Engine { get; init; }

    public DateTime ScannedAtUtc { get; init; } = DateTime.UtcNow;
    public TimeSpan Duration { get; init; }

    public string? VolumeLabel { get; init; }
    public string? FileSystem { get; init; }
    public long? VolumeTotalBytes { get; init; }
    public long? VolumeFreeBytes { get; init; }
    public int ClusterSize { get; init; }

    /// <summary>Directories that could not be read.</summary>
    public int ErrorCount { get; init; }

    /// <summary>NTFS change journal position when the scan started; lets a later refresh replay only what changed.</summary>
    public UsnCheckpoint? Usn { get; init; }
}

public readonly record struct UsnCheckpoint(ulong JournalId, long NextUsn);
