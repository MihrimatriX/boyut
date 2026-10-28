using Boyut.Core.Analysis;
using Boyut.Core.Layout;
using Boyut.Core.Model;
using Boyut.Core.Search;

namespace Boyut.Core.Tests;

public class SearchTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A small C: drive in the shape of a real one.</summary>
    internal static FileIndex Drive()
    {
        var b = new FileIndexBuilder();
        int root = b.AddRoot(@"C:\");
        int Dir(int parent, string name, FileAttributes extra = 0) =>
            b.Add(parent, name, 0, 0, 0, (int)(FileAttributes.Directory | extra), NodeFlags.Directory);
        void File(int parent, string name, long size, DateTime modified, FileAttributes attrs = 0, NodeFlags flags = NodeFlags.None) =>
            b.Add(parent, name, size, size, modified.Ticks, (int)attrs, flags);

        int users = Dir(root, "Users");
        int ali = Dir(users, "Ali");
        int videos = Dir(ali, "Videos");
        File(videos, "Ekran kaydı.mp4", 9L << 30, new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));
        File(videos, "Fragman_ham_4K.mkv", 7L << 30, new DateTime(2025, 11, 3, 0, 0, 0, DateTimeKind.Utc));
        File(videos, "kısa.mov", 200L << 20, new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc));
        int dl = Dir(ali, "Downloads");
        File(dl, "ubuntu-26.04.iso", 6L << 30, new DateTime(2026, 4, 25, 0, 0, 0, DateTimeKind.Utc));
        File(dl, "setup.exe", 300L << 20, new DateTime(2023, 2, 11, 0, 0, 0, DateTimeKind.Utc));
        File(dl, "kapak (1).psd", 1480L << 20, new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc));
        int pics = Dir(ali, "Pictures");
        File(pics, "IMG_4821.HEIC", 3L << 20, new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc));
        File(pics, "IMG_0117.jpg", 4L << 20, new DateTime(2023, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        File(pics, "thumbs.db", 1L << 10, new DateTime(2023, 7, 15, 0, 0, 0, DateTimeKind.Utc), FileAttributes.Hidden | FileAttributes.System);
        int proj = Dir(ali, "Projeler");
        int web = Dir(proj, "web");
        File(web, "package.json", 1200, Now.AddDays(-1));
        int nm = Dir(web, "node_modules");
        int lib = Dir(nm, "lib");
        File(lib, "index.js", 2L << 20, Now.AddDays(-1));
        int game = Dir(proj, "gold-and-brown");
        Dir(game, "Assets");
        Dir(game, "ProjectSettings");
        int library = Dir(game, "Library");
        File(library, "ArtifactDB", 3L << 30, Now.AddDays(-2));
        int dotnet = Dir(proj, "Inlay");
        File(dotnet, "Inlay.csproj", 3000, Now.AddDays(-3));
        int bin = Dir(dotnet, "bin");
        File(bin, "Inlay.dll", 5L << 20, Now.AddDays(-3));
        int appdata = Dir(ali, "AppData", FileAttributes.Hidden);
        int local = Dir(appdata, "Local");
        int temp = Dir(local, "Temp");
        File(temp, "debug.log", 920L << 20, Now);
        File(temp, "crash.dmp", 2L << 30, new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc));
        int windows = Dir(root, "Windows", FileAttributes.System);
        int winsxs = Dir(windows, "WinSxS");
        File(winsxs, "component.dll", 8L << 30, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), FileAttributes.System);
        File(windows, "big-old.log", 2L << 30, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File(root, "pagefile.sys", 16L << 30, Now, FileAttributes.Hidden | FileAttributes.System);
        File(root, "hiberfil.sys", 12L << 30, Now, FileAttributes.Hidden | FileAttributes.System);
        int rb = Dir(root, "$Recycle.Bin", FileAttributes.Hidden | FileAttributes.System);
        File(rb, "$R123.zip", 4L << 30, Now.AddDays(-20));
        File(root, "arşiv.zip", 2L << 30, new DateTime(2022, 12, 30, 0, 0, 0, DateTimeKind.Utc));
        return b.Build(new ScanMetadata { RootPath = @"C:\", Separator = '\\', Engine = "test" });
    }

    private static readonly FileIndex Index = Drive();

    private static string[] Names(string query, QueryMode mode = QueryMode.Glob, QueryOptions? options = null)
    {
        var q = Query.Parse(query, mode, options, Now);
        Assert.True(q.IsValid, q.Error);
        return SearchEngine.Search(Index, q).Nodes.Select(Index.GetName).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void Glob_alternatives_and_size()
    {
        Assert.Equal(["Ekran kaydı.mp4", "Fragman_ham_4K.mkv"], Names("*.mp4;*.mkv;*.mov size:>500mb"));
        Assert.Equal(["Ekran kaydı.mp4", "Fragman_ham_4K.mkv", "kısa.mov"], Names("*.mp4;*.mkv;*.mov"));
    }

    [Fact]
    public void Glob_question_mark_and_classes()
    {
        Assert.Equal(["IMG_0117.jpg", "IMG_4821.HEIC"], Names("IMG_????.*"));
        Assert.Equal(["IMG_4821.HEIC"], Names("IMG_[4-9]*"));
        Assert.Equal(["IMG_0117.jpg"], Names("IMG_[!4-9]*"));
    }

    [Fact]
    public void Glob_double_star_matches_paths()
    {
        Assert.Equal(["node_modules"], Names(@"**\node_modules"));
        Assert.Equal(["index.js"], Names(@"node_modules\**\*.js"));
        Assert.Equal(["Ekran kaydı.mp4", "Fragman_ham_4K.mkv", "kısa.mov"], Names(@"Videos\*"));
    }

    [Fact]
    public void Plain_words_match_anywhere_in_the_name()
    {
        Assert.Equal(["Fragman_ham_4K.mkv"], Names("fragman"));
        Assert.Equal(["kapak (1).psd"], Names("\"(1)\""));
    }

    [Fact]
    public void Regex_mode()
    {
        Assert.Equal(["IMG_0117.jpg", "IMG_4821.HEIC"], Names(@"^IMG_\d{4}\.(jpg|heic)$", QueryMode.Regex));
        Assert.Equal(["kapak (1).psd"], Names(@"\(\d+\)\.\w+$", QueryMode.Regex));
        // look-behind is not supported by the linear engine, falls back to backtracking with a timeout
        Assert.Equal(["Fragman_ham_4K.mkv"], Names(@"(?<=_)4K\.mkv$", QueryMode.Regex));
    }

    [Fact]
    public void Invalid_regex_reports_error()
    {
        var q = Query.Parse("([a-", QueryMode.Regex);
        Assert.False(q.IsValid);
        Assert.Contains("regex", q.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(SearchEngine.Search(Index, q).Nodes);
    }

    [Fact]
    public void Text_mode_requires_every_word()
    {
        Assert.Equal(["Fragman_ham_4K.mkv"], Names("fragman 4k", QueryMode.Text));
        Assert.Empty(Names("fragman 8k", QueryMode.Text));
    }

    [Fact]
    public void Keys_ext_type_kind()
    {
        Assert.Equal(["IMG_0117.jpg", "IMG_4821.HEIC", "kapak (1).psd"], Names("type:image"));
        Assert.Equal(["arşiv.zip", "ubuntu-26.04.iso"], Names("ext:iso,zip -in:Recycle"));
        Assert.Contains("node_modules", Names("kind:dir node"));
        Assert.DoesNotContain("node_modules", Names("kind:file node"));
    }

    [Fact]
    public void Turkish_key_aliases()
    {
        Assert.Equal(Names("type:video size:>1gb"), Names("tür:video boyut:>1gb"));
        Assert.Equal(Names("ext:iso"), Names("uzantı:iso"));
    }

    [Fact]
    public void Size_ranges_and_empty()
    {
        Assert.Equal(["debug.log", "kapak (1).psd", "setup.exe"], Names("size:250mb..1,5gb -kind:dir"));
        Assert.Empty(Names("size:empty kind:file"));
    }

    [Fact]
    public void Dates_and_ages()
    {
        Assert.Equal(["IMG_0117.jpg", "setup.exe", "thumbs.db"], Names("modified:2023"));
        Assert.Equal(["arşiv.zip", "big-old.log", "component.dll"], Names("modified:<2023 kind:file"));
        Assert.Equal(["ubuntu-26.04.iso"], Names("modified:2026-04"));
        Assert.Equal(["ubuntu-26.04.iso"], Names("modified:25.04.2026"));
        Assert.Contains("setup.exe", Names("age:>1y kind:file"));
        Assert.DoesNotContain("setup.exe", Names("age:<30d"));
        Assert.Contains("debug.log", Names("age:<1w kind:file"));
    }

    [Fact]
    public void Attributes_and_negation()
    {
        Assert.Equal(["hiberfil.sys", "pagefile.sys", "thumbs.db"], Names("attr:hidden,system kind:file"));
        Assert.DoesNotContain("thumbs.db", Names("* -attr:hidden"));
        Assert.DoesNotContain("debug.log", Names("*.log;*.dmp -*.log"));
    }

    [Fact]
    public void Location_filter()
    {
        Assert.Equal(["kapak (1).psd", "setup.exe", "ubuntu-26.04.iso"], Names("in:Downloads"));
        Assert.Equal(["crash.dmp", "debug.log"], Names(@"in:""AppData\Local"" kind:file"));
    }

    [Fact]
    public void Bad_keys_explain_themselves()
    {
        Assert.Contains("Boyut", Query.Parse("size:>abc").Error);
        Assert.Contains("Tarih", Query.Parse("modified:yarın").Error);
        Assert.Contains("öznitelik", Query.Parse("attr:shiny").Error);
    }

    [Fact]
    public void Tokens_are_reported_for_chips()
    {
        var q = Query.Parse("*.mp4 size:>1gb -in:Temp", QueryMode.Glob);
        Assert.Collection(q.Tokens,
            t => Assert.Equal((QueryTokenKind.Pattern, "*.mp4", false), (t.Kind, t.Value, t.Negated)),
            t => Assert.Equal((QueryTokenKind.Size, ">1gb", false), (t.Kind, t.Value, t.Negated)),
            t => Assert.Equal((QueryTokenKind.Location, "Temp", true), (t.Kind, t.Value, t.Negated)));
    }

    [Fact]
    public void Totals_do_not_double_count_nested_matches()
    {
        var q = Query.Parse("node_modules;index.js");
        var r = SearchEngine.Search(Index, q);
        Assert.Equal(2, r.TotalMatches);
        Assert.Equal(2L << 20, r.TotalBytes);
    }

    [Fact]
    public void Scope_and_truncation()
    {
        int videos = Index.FindByPath(@"C:\Users\Ali\Videos");
        var r = SearchEngine.Search(Index, Query.Parse("*"), videos, maxResults: 2);
        Assert.Equal(3, r.TotalMatches);
        Assert.True(r.Truncated);
        Assert.Equal(["Ekran kaydı.mp4", "Fragman_ham_4K.mkv"], r.Nodes.Select(Index.GetName));
    }

    [Fact]
    public void Case_sensitivity_option()
    {
        Assert.Single(Names("*.heic"));
        Assert.Empty(Names("*.heic", options: new QueryOptions { CaseSensitive = true }));
    }
}

public class LayoutAndAnalysisTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Squarified_rectangles_cover_the_area_without_overlap()
    {
        double[] values = [6, 6, 4, 3, 2, 2, 1, 0];
        var rects = Squarifier.Layout(values, 0, 0, 600, 400);

        double total = values.Sum();
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i] / total * 600 * 400, rects[i].Area, 6);
            Assert.True(rects[i].X >= -1e-9 && rects[i].Y >= -1e-9);
            Assert.True(rects[i].X + rects[i].Width <= 600 + 1e-6 && rects[i].Y + rects[i].Height <= 400 + 1e-6);
        }
        for (int i = 0; i < rects.Length; i++)
            for (int j = i + 1; j < rects.Length; j++)
            {
                var a = rects[i]; var b = rects[j];
                double ox = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                double oy = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                Assert.False(ox > 1e-6 && oy > 1e-6, $"{i} ile {j} çakışıyor");
            }
        // squarified: the big ones are reasonably square
        Assert.True(Math.Max(rects[0].Width / rects[0].Height, rects[0].Height / rects[0].Width) < 3);
    }

    [Fact]
    public void Type_breakdown_and_largest_files()
    {
        var index = SearchTests.Drive();
        var types = TypeStatistics.Compute(index);
        Assert.Equal(index.TotalSize, types.TotalBytes);
        Assert.Equal(FileCategory.System, types.Categories[0].Category); // pagefile + hiberfil + logs
        Assert.Contains(types.Extensions, e => e.Extension == "mp4" && e.Files == 1);

        var largest = TypeStatistics.LargestFiles(index, count: 3).Select(index.GetName).ToArray();
        Assert.Equal(["pagefile.sys", "hiberfil.sys", "Ekran kaydı.mp4"], largest);
    }

    [Fact]
    public void Cleanup_rules_recognise_known_folders()
    {
        var index = SearchTests.Drive();
        var findings = CleanupAnalyzer.Analyze(index, nowUtc: Now).ToDictionary(f => f.Rule.Id);

        Assert.Equal(2L << 20, findings["node-modules"].Bytes);
        Assert.Equal(3L << 30, findings["unity-library"].Bytes);
        Assert.Equal(5L << 20, findings["dotnet-build"].Bytes);
        Assert.Equal((920L << 20) + (2L << 30), findings["user-temp"].Bytes); // dump inside Temp is not counted twice
        Assert.False(findings.ContainsKey("dump-files"));
        Assert.Equal(4L << 30, findings["recycle"].Bytes);
        Assert.Equal(CleanupSafety.Keep, findings["winsxs"].Rule.Safety);
        Assert.Equal(16L << 30, findings["pagefile"].Bytes);
        Assert.Equal(12L << 30, findings["hiberfil"].Bytes);
        Assert.Equal(["ubuntu-26.04.iso", "setup.exe"], findings["old-downloads"].Nodes.Select(index.GetName)); // older than 90 days; kapak.psd is not an installer
        // large-old: arşiv.zip (2022, 2 GB) but not Windows\big-old.log (system zone)
        Assert.Equal(["arşiv.zip"], findings["large-old"].Nodes.Select(index.GetName));
        // Keep rules sort last
        Assert.Equal(CleanupSafety.Keep, CleanupAnalyzer.Analyze(index, nowUtc: Now)[^1].Rule.Safety);
    }

    [Fact]
    public void Cleanup_does_not_fire_without_markers()
    {
        var b = new FileIndexBuilder();
        int root = b.AddRoot("/data");
        int lib = b.Add(root, "Library", 0, 0, 0, 0, NodeFlags.Directory);   // no Assets/ProjectSettings
        b.Add(lib, "x", 100, 100, 0, 0, NodeFlags.None);
        int nm = b.Add(root, "node_modules", 0, 0, 0, 0, NodeFlags.Directory); // no package.json
        b.Add(nm, "y", 100, 100, 0, 0, NodeFlags.None);
        var index = b.Build(new ScanMetadata { RootPath = "/data", Separator = '/', Engine = "test" });
        Assert.Empty(CleanupAnalyzer.Analyze(index, nowUtc: Now));
    }
}

public class DuplicateFinderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "boyut-dupes-" + Guid.NewGuid().ToString("N"));

    public DuplicateFinderTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        var big = new byte[300_000];
        new Random(42).NextBytes(big);
        File.WriteAllBytes(Path.Combine(root, "a", "film.bin"), big);
        File.WriteAllBytes(Path.Combine(root, "b", "film kopya.bin"), big);
        var almost = (byte[])big.Clone();
        almost[150_000] ^= 0xFF; // same size, same edges, different middle
        File.WriteAllBytes(Path.Combine(root, "b", "film-farkli.bin"), almost);
        var small = new byte[5_000];
        new Random(7).NextBytes(small);
        File.WriteAllBytes(Path.Combine(root, "a", "s1.dat"), small);
        File.WriteAllBytes(Path.Combine(root, "b", "s2.dat"), small);
        File.WriteAllBytes(Path.Combine(root, "b", "tiny.txt"), [1, 2, 3]);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Finds_content_duplicates_only()
    {
        var index = await new Scanning.ManagedScanner().ScanAsync(root, new Scanning.ScanOptions(), cancellationToken: TestContext.Current.CancellationToken);
        var groups = await DuplicateFinder.FindAsync(index, options: new DuplicateOptions { MinimumSize = 1000, SkipSystem = false },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, groups.Count);
        var bigGroup = groups.Single(g => g.Size == 300_000);
        Assert.Equal(["film kopya.bin", "film.bin"], bigGroup.Nodes.Select(index.GetName).Order(StringComparer.Ordinal));
        Assert.Equal(300_000, bigGroup.Wasted);
        Assert.Equal(2, groups.Single(g => g.Size == 5_000).Nodes.Length);
    }
}
