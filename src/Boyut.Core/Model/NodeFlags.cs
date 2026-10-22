namespace Boyut.Core.Model;

/// <summary>Scanner-level facts about a node that are not plain file attributes.</summary>
[Flags]
public enum NodeFlags : byte
{
    None = 0,
    Directory = 1 << 0,

    /// <summary>
    /// Second (third, …) name of a hard-linked file. Shown in the tree, but its size is not
    /// added to the parent's total, so the same bytes are never counted twice.
    /// </summary>
    HardLinkSecondary = 1 << 1,

    /// <summary>Cloud-only placeholder (OneDrive etc.). Reading it would download it.</summary>
    CloudPlaceholder = 1 << 2,

    /// <summary>Junction / symbolic link. Not followed unless the scan asks for it.</summary>
    ReparsePoint = 1 << 3,

    /// <summary>The directory could not be (fully) read; its total may be too small.</summary>
    AccessDenied = 1 << 4,
}

/// <summary>Win32 attribute bits that <see cref="FileAttributes"/> does not name.</summary>
public static class ExtraFileAttributes
{
    public const int RecallOnOpen = 0x0004_0000;
    public const int Pinned = 0x0008_0000;
    public const int Unpinned = 0x0010_0000;
    public const int RecallOnDataAccess = 0x0040_0000;

    public static bool IsCloudPlaceholder(int attributes) =>
        (attributes & (RecallOnDataAccess | RecallOnOpen)) != 0 ||
        ((attributes & (int)FileAttributes.Offline) != 0);
}
