using System.Collections.Frozen;
using Boyut.Core.Model;

namespace Boyut.Core.Analysis;

public enum FileCategory
{
    Folder,
    Video,
    Audio,
    Image,
    Document,
    Archive,
    DiskImage,
    Code,
    Executable,
    GameAsset,
    System,
    Other,
}

public static class FileCategories
{
    private static readonly FrozenDictionary<string, FileCategory> ByExtension = Build();
    private static readonly FrozenDictionary<string, FileCategory>.AlternateLookup<ReadOnlySpan<char>> Lookup =
        ByExtension.GetAlternateLookup<ReadOnlySpan<char>>();

    public static FileCategory Classify(FileIndex index, int node)
    {
        if (index.IsDirectory(node)) return FileCategory.Folder;
        return Classify(index.ExtensionSpan(node));
    }

    public static FileCategory Classify(ReadOnlySpan<char> extension) =>
        !extension.IsEmpty && Lookup.TryGetValue(extension, out var category) ? category : FileCategory.Other;

    public static string DisplayName(FileCategory category) => category switch
    {
        FileCategory.Folder => "Klasör",
        FileCategory.Video => "Video",
        FileCategory.Audio => "Ses",
        FileCategory.Image => "Görsel",
        FileCategory.Document => "Belge",
        FileCategory.Archive => "Arşiv",
        FileCategory.DiskImage => "Disk imajı",
        FileCategory.Code => "Kod",
        FileCategory.Executable => "Uygulama",
        FileCategory.GameAsset => "Oyun ve 3D",
        FileCategory.System => "Sistem ve log",
        _ => "Diğer",
    };

    /// <summary>Accepts English and Turkish names (with or without Turkish characters).</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out FileCategory category)
    {
        string t = Normalize(text);
        category = t switch
        {
            "folder" or "dir" or "klasor" => FileCategory.Folder,
            "video" or "film" => FileCategory.Video,
            "audio" or "music" or "ses" or "muzik" => FileCategory.Audio,
            "image" or "picture" or "photo" or "gorsel" or "resim" or "foto" => FileCategory.Image,
            "document" or "doc" or "belge" => FileCategory.Document,
            "archive" or "arsiv" or "zip" => FileCategory.Archive,
            "diskimage" or "disk" or "imaj" or "iso" => FileCategory.DiskImage,
            "code" or "source" or "kod" => FileCategory.Code,
            "app" or "exe" or "executable" or "program" or "uygulama" => FileCategory.Executable,
            "game" or "3d" or "asset" or "oyun" => FileCategory.GameAsset,
            "system" or "log" or "sistem" => FileCategory.System,
            "other" or "diger" => FileCategory.Other,
            _ => (FileCategory)(-1),
        };
        return (int)category >= 0;
    }

    internal static string Normalize(ReadOnlySpan<char> text)
    {
        Span<char> buffer = stackalloc char[Math.Min(text.Length, 64)];
        int n = 0;
        foreach (char ch in text)
        {
            if (n == buffer.Length) break;
            buffer[n++] = char.ToLowerInvariant(ch) switch
            {
                'ç' => 'c', 'ğ' => 'g', 'ı' => 'i', 'ö' => 'o', 'ş' => 's', 'ü' => 'u', 'İ' => 'i',
                var c => c,
            };
        }
        return new string(buffer[..n]);
    }

    private static FrozenDictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, string list)
        {
            foreach (var ext in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map[ext] = c;
        }

        Add(FileCategory.Video, "mp4 m4v mkv mov avi wmv webm flv mpg mpeg m2ts mts ts 3gp vob ogv prproj veg");
        Add(FileCategory.Audio, "mp3 wav flac aac m4a ogg opus wma aiff aif mid midi ape alac bank fsb wem");
        Add(FileCategory.Image, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng psd psb ai svg ico tga exr hdr kra xcf avif jxl");
        Add(FileCategory.Document, "pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt md csv epub mobi pages key numbers one xps");
        Add(FileCategory.Archive, "zip 7z rar tar gz tgz bz2 xz zst lz4 cab arj lzh nupkg whl jar apk aab");
        Add(FileCategory.DiskImage, "iso img vhd vhdx vmdk vdi qcow2 wim esd dmg");
        Add(FileCategory.Code, "cs csproj sln slnx js mjs cjs ts tsx jsx json xml yaml yml toml html htm css scss less py rb go rs java kt kts swift c cc cpp cxx h hpp m mm lua php sh ps1 psm1 bat cmd sql razor cshtml vue svelte shader hlsl glsl compute uxml uss pyc pyo ipynb");
        Add(FileCategory.Executable, "exe dll msi msix msixbundle appx appxbundle ocx so dylib appimage deb rpm pdb lib a o pyd");
        Add(FileCategory.GameAsset, "unity unitypackage asset prefab mat fbx obj blend blend1 max ma mb 3ds dae gltf glb usd usdz uasset umap upk pak ucas utoc bundle vpk bsp");
        Add(FileCategory.System, "sys log dmp mdmp etl evtx tmp temp bak old cache db db-wal db-shm sqlite ldb dat ini cfg reg lnk mui cat inf manifest etl");
        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
