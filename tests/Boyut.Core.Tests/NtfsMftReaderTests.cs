using System.IO.Compression;
using Boyut.Core.Model;
using Boyut.Core.Ntfs;
using Boyut.Core.Scanning;

namespace Boyut.Core.Tests;

/// <summary>
/// Runs the MFT reader against a real NTFS image produced by mkntfs/ntfs-3g
/// (see Fixtures/make-ntfs.sh for exactly what it contains).
/// </summary>
public class NtfsMftReaderTests
{
    private static readonly Lazy<byte[]> Image = new(() =>
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ntfs-sample.img.gz");
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    });

    private static FileIndex Read(bool streams = false, bool hidden = true)
    {
        using var reader = new StreamVolumeReader(new MemoryStream(Image.Value, writable: false));
        return NtfsMftReader.ReadVolume(reader, @"X:\",
            new ScanOptions { IncludeAlternateStreams = streams, IncludeHidden = hidden });
    }

    private static int Find(FileIndex index, string relative)
    {
        int node = index.FindByPath(@"X:\" + relative);
        Assert.True(node >= 0, $"'{relative}' bulunamadı");
        return node;
    }

    [Fact]
    public void Reads_boot_sector()
    {
        var boot = NtfsBootSector.Parse(Image.Value.AsSpan(0, 512));
        Assert.Equal(4096, boot.ClusterSize);
        Assert.Equal(1024, boot.FileRecordSize);
    }

    [Fact]
    public void Non_resident_file_has_real_size_and_allocation()
    {
        var index = Read();
        int big = Find(index, @"Users\Ali\Downloads\big.bin");
        Assert.Equal(5_242_880, index.Size(big));
        Assert.Equal(5_242_880, index.Allocated(big));
        Assert.False(index.IsDirectory(big));
    }

    [Fact]
    public void Resident_file_uses_no_clusters()
    {
        var index = Read();
        int tiny = Find(index, @"Users\Ali\Downloads\tiny.txt");
        Assert.Equal(5, index.Size(tiny));
        Assert.Equal(0, index.Allocated(tiny));
    }

    [Fact]
    public void Hard_link_is_listed_twice_but_counted_once()
    {
        var index = Read();
        int docs = Find(index, @"Users\Ali\Documents");
        int a = Find(index, @"Users\Ali\Documents\report.pdf");
        int b = Find(index, @"Users\Ali\Documents\report-link.pdf");

        Assert.Equal(307_200, index.Size(a));
        Assert.Equal(307_200, index.Size(b));
        int secondaries = new[] { a, b }.Count(n => (index.Flags(n) & NodeFlags.HardLinkSecondary) != 0);
        Assert.Equal(1, secondaries);
        Assert.Equal(307_200, index.Size(docs));
        Assert.Equal(2, index.FileCount(docs));
    }

    [Fact]
    public void Alternate_streams_are_optional()
    {
        int Report(FileIndex i) => Find(i, @"Users\Ali\Documents\report.pdf");
        var without = Read(streams: false);
        var with = Read(streams: true);
        Assert.Equal(307_200, without.Size(Report(without)));
        Assert.Equal(307_200 + 1024, with.Size(Report(with)));
    }

    [Fact]
    public void Sparse_file_reports_tiny_allocation()
    {
        var index = Read();
        int sparse = Find(index, "sparse.dat");
        Assert.Equal(104_857_600, index.Size(sparse));
        Assert.True(index.Allocated(sparse) < 1 << 20, $"allocated = {index.Allocated(sparse)}");
        Assert.True((index.Attributes(sparse) & FileAttributes.SparseFile) != 0);
    }

    [Fact]
    public void Deleted_files_do_not_appear()
    {
        var index = Read();
        Assert.Equal(-1, index.FindByPath(@"X:\deleted.txt"));
    }

    [Fact]
    public void Unicode_names_survive()
    {
        var index = Read();
        int node = Find(index, "Çalışma Belgesi ğüşiöç.docx");
        Assert.Equal("Çalışma Belgesi ğüşiöç.docx", index.GetName(node));
        Assert.Equal(2000, index.Size(node));
    }

    [Fact]
    public void Large_directory_is_complete()
    {
        var index = Read();
        int many = Find(index, "many");
        Assert.Equal(3000, index.ChildCount(many));
        Assert.Equal(3000, index.FileCount(many));
        Assert.Equal(3000 * 4, index.Size(many));
    }

    [Fact]
    public void Metadata_files_follow_the_hidden_option()
    {
        Assert.True(Read(hidden: true).FindByPath(@"X:\$MFT") > 0);
        Assert.Equal(-1, Read(hidden: false).FindByPath(@"X:\$MFT"));
    }

    [Fact]
    public void Paths_and_subtrees()
    {
        var index = Read();
        int js = Find(index, @"Projects\web\node_modules\lib\index.js");
        Assert.Equal(@"X:\Projects\web\node_modules\lib\index.js", index.GetFullPath(js));

        int users = Find(index, "Users");
        var sub = index.Subtree(users);
        Assert.Equal(@"X:\Users", sub.Metadata.RootPath);
        Assert.Equal(index.Size(users), sub.TotalSize);
        Assert.Equal(index.FileCount(users), sub.TotalFiles);
        Assert.True(sub.FindByPath(@"X:\Users\Ali\Downloads\big.bin") > 0);
    }

    [Fact]
    public void Children_are_sorted_by_size()
    {
        var index = Read();
        var kids = index.Children(FileIndex.RootNode);
        for (int i = 1; i < kids.Length; i++)
            Assert.True(index.Size(kids[i - 1]) >= index.Size(kids[i]));
    }
}
