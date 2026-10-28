using System.Globalization;
using System.Text;
using System.Text.Json;
using Boyut.Core.Analysis;
using Boyut.Core.Formatting;
using Boyut.Core.Model;
using Boyut.Core.Persistence;
using Boyut.Core.Scanning;
using Boyut.Core.Search;
using Boyut.Platform.Windows;

namespace Boyut.Cli;

internal static class Program
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static bool color;

    private static async Task<int> Main(string[] args)
    {
        if (ScanHelperHost.IsHelperInvocation(args)) return await ScanHelperHost.RunAsync(args);

        Console.OutputEncoding = Encoding.UTF8;
        color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        CultureInfo.CurrentCulture = Tr;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?") { PrintHelp(); return 0; }
        if (args[0] is "--version" or "version")
        {
            Console.WriteLine($"boyut {typeof(Program).Assembly.GetName().Version?.ToString(3)}");
            return 0;
        }

        var a = new Args(args.Skip(1).ToArray());
        try
        {
            return args[0] switch
            {
                "scan" or "tara" => await Scan(a, cts.Token),
                "search" or "ara" => await Search(a, cts.Token),
                "types" or "turler" => await Types(a, cts.Token),
                "cleanup" or "temizlik" => await Cleanup(a, cts.Token),
                "dupes" or "kopyalar" => await Dupes(a, cts.Token),
                "diff" or "fark" => Diff(a),
                "refresh" or "guncelle" => Refresh(a, cts.Token),
                _ => Usage($"Bilinmeyen komut: {args[0]}"),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("\nİptal edildi.");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine(Paint("Hata: ", "31") + ex.Message);
            return 1;
        }
    }

    // ---- commands -----------------------------------------------------------------------------

    private static async Task<int> Scan(Args a, CancellationToken ct)
    {
        if (a.Positional.Count < 1) return Usage("Taranacak klasörü yazın: boyut scan C:\\");
        var index = await LoadOrScan(a.Positional[0], a, ct);
        int top = a.Int("--top", 20);
        int depth = a.Int("--depth", 1);

        PrintSummary(index);
        PrintTree(index, FileIndex.RootNode, depth, top);

        if (a.Value("--save") is { } save)
        {
            if (!save.EndsWith(SnapshotSerializer.FileExtension, StringComparison.OrdinalIgnoreCase)) save += SnapshotSerializer.FileExtension;
            SnapshotSerializer.Save(index, save);
            Console.WriteLine($"Tarama kaydedildi: {save}");
        }
        if (a.Value("--csv") is { } csv)
        {
            CsvExporter.ExportToFile(csv, w => CsvExporter.ExportTree(index, w, maxDepth: a.Int("--csv-depth", int.MaxValue), culture: Tr));
            Console.WriteLine($"CSV yazıldı: {csv}");
        }
        if (a.Value("--json") is { } json)
        {
            WriteJson(index, json, a.Int("--json-depth", 3));
            Console.WriteLine($"JSON yazıldı: {json}");
        }
        return 0;
    }

    private static async Task<int> Search(Args a, CancellationToken ct)
    {
        if (a.Positional.Count < 2) return Usage("Kullanım: boyut search <klasör|tarama.boyut> <sorgu>");
        var index = await LoadOrScan(a.Positional[0], a, ct);
        var mode = a.Value("--mode")?.ToLowerInvariant() switch
        {
            "regex" or "re" => QueryMode.Regex,
            "text" or "metin" => QueryMode.Text,
            _ => QueryMode.Glob,
        };
        var query = Query.Parse(string.Join(' ', a.Positional.Skip(1)), mode, new QueryOptions
        {
            CaseSensitive = a.Flag("--case"),
            MatchFullPath = a.Flag("--full-path"),
            IncludeDirectories = !a.Flag("--files-only"),
            IncludeFiles = !a.Flag("--dirs-only"),
        });
        if (!query.IsValid) { Console.Error.WriteLine(Paint("Sorgu hatası: ", "31") + query.Error); return 2; }

        int limit = a.Int("--limit", 50);
        var result = SearchEngine.Search(index, query, maxResults: Math.Max(limit, 1), cancellationToken: ct);

        Console.WriteLine(Paint($"{SizeFormatter.FormatCount(result.TotalMatches)} sonuç", "1") +
                          $" · {SizeFormatter.Format(result.TotalBytes)} · {SizeFormatter.FormatDuration(result.Elapsed)}");
        Console.WriteLine(Paint("Çözümlenen: ", "2") + string.Join("  ", query.Tokens.Select(t => (t.Negated ? "−" : "") + t.KindLabel + ":" + t.Value)));
        Console.WriteLine();
        foreach (int node in result.Nodes)
        {
            string size = SizeFormatter.Format(index.Size(node)).PadLeft(10);
            string date = index.ModifiedUtc(node)?.ToLocalTime().ToString("dd.MM.yyyy", Tr) ?? "          ";
            string kind = index.IsDirectory(node) ? Paint("▸", "33") : " ";
            Console.WriteLine($"{size}  {date}  {kind} {index.GetFullPath(node)}");
        }
        if (result.Truncated) Console.WriteLine(Paint($"… ilk {limit} gösterildi (--limit ile artırın)", "2"));

        if (a.Value("--csv") is { } csv)
        {
            var all = SearchEngine.Search(index, query, maxResults: int.MaxValue, cancellationToken: ct);
            CsvExporter.ExportToFile(csv, w => CsvExporter.ExportNodes(index, w, all.Nodes, Tr));
            Console.WriteLine($"CSV yazıldı: {csv}");
        }
        return 0;
    }

    private static async Task<int> Types(Args a, CancellationToken ct)
    {
        if (a.Positional.Count < 1) return Usage("Kullanım: boyut types <klasör|tarama.boyut>");
        var index = await LoadOrScan(a.Positional[0], a, ct);
        var stats = TypeStatistics.Compute(index, topExtensions: a.Int("--top", 15));

        Console.WriteLine(Paint("Kategoriler", "1"));
        foreach (var c in stats.Categories)
        {
            double f = stats.TotalBytes == 0 ? 0 : (double)c.Bytes / stats.TotalBytes;
            Console.WriteLine($"  {c.Name,-15} {SizeFormatter.Format(c.Bytes),10}  {Bar(f, 24)} {SizeFormatter.FormatPercent(f),7}  {SizeFormatter.FormatCount(c.Files),10} dosya");
        }
        Console.WriteLine();
        Console.WriteLine(Paint("Uzantılar", "1"));
        foreach (var e in stats.Extensions)
        {
            double f = stats.TotalBytes == 0 ? 0 : (double)e.Bytes / stats.TotalBytes;
            Console.WriteLine($"  .{e.Extension,-14} {SizeFormatter.Format(e.Bytes),10}  {Bar(f, 24)} {SizeFormatter.FormatPercent(f),7}  {SizeFormatter.FormatCount(e.Files),10} dosya");
        }
        Console.WriteLine();
        Console.WriteLine(Paint("En büyük dosyalar", "1"));
        foreach (int node in TypeStatistics.LargestFiles(index, count: 10))
            Console.WriteLine($"  {SizeFormatter.Format(index.Size(node)),10}  {index.GetFullPath(node)}");
        return 0;
    }

    private static async Task<int> Cleanup(Args a, CancellationToken ct)
    {
        if (a.Positional.Count < 1) return Usage("Kullanım: boyut cleanup <klasör|tarama.boyut>");
        var index = await LoadOrScan(a.Positional[0], a, ct);
        var findings = CleanupAnalyzer.Analyze(index, cancellationToken: ct);

        long safe = findings.Where(f => f.Rule.Safety == CleanupSafety.Safe).Sum(f => f.Bytes);
        long caution = findings.Where(f => f.Rule.Safety == CleanupSafety.Caution).Sum(f => f.Bytes);
        Console.WriteLine(Paint($"Güvenle kazanılabilir: {SizeFormatter.Format(safe)}", "1;32") +
                          $"   ·   gözden geçirilecek: {SizeFormatter.Format(caution)}");
        Console.WriteLine();

        foreach (var f in findings)
        {
            var (badge, code) = f.Rule.Safety switch
            {
                CleanupSafety.Safe => ("GÜVENLİ", "32"),
                CleanupSafety.Caution => ("DİKKAT ", "33"),
                _ => ("DOKUNMA", "36"),
            };
            Console.WriteLine($"{Paint(badge, code)}  {SizeFormatter.Format(f.Bytes),10}  {Paint(f.Rule.Title, "1")}{(f.Rule.RequiresAdmin ? Paint("  (yönetici)", "2") : "")}");
            Console.WriteLine(Paint($"           {f.Rule.Description}", "2"));
            if (f.Rule.Advice is { } advice) Console.WriteLine($"           → {advice}");
            foreach (int node in f.Nodes.Take(a.Int("--show", 3)))
                Console.WriteLine(Paint($"             {SizeFormatter.Format(index.Size(node)),10}  {index.GetFullPath(node)}", "2"));
            if (f.Nodes.Length > a.Int("--show", 3))
                Console.WriteLine(Paint($"             … ve {f.Nodes.Length - a.Int("--show", 3)} konum daha", "2"));
            Console.WriteLine();
        }
        return 0;
    }

    private static async Task<int> Dupes(Args a, CancellationToken ct)
    {
        if (a.Positional.Count < 1) return Usage("Kullanım: boyut dupes <klasör|tarama.boyut> [--min-size 1mb]");
        var index = await LoadOrScan(a.Positional[0], a, ct);
        long min = 1 << 20;
        if (a.Value("--min-size") is { } m && !SizeFormatter.TryParse(m, out min)) return Usage("--min-size anlaşılamadı");

        var options = new DuplicateOptions
        {
            MinimumSize = min,
            IdentityProvider = OperatingSystem.IsWindows() ? WindowsShell.GetFileIdentity : null,
        };
        var progress = new Progress<DuplicateProgress>(p =>
        {
            if (!Console.IsErrorRedirected) Console.Error.Write($"\r{p.Phase}: {p.Done:N0}/{p.Total:N0}        ");
        });
        var groups = await DuplicateFinder.FindAsync(index, options: options, progress: progress, cancellationToken: ct);
        if (!Console.IsErrorRedirected) Console.Error.Write("\r" + new string(' ', 70) + "\r");

        Console.WriteLine(Paint($"{groups.Count} kopya grubu · boşa giden alan {SizeFormatter.Format(groups.Sum(g => g.Wasted))}", "1"));
        foreach (var g in groups.Take(a.Int("--top", 25)))
        {
            Console.WriteLine();
            Console.WriteLine($"{SizeFormatter.Format(g.Size)} × {g.Nodes.Length}  →  {Paint(SizeFormatter.Format(g.Wasted) + " boşa", "33")}");
            foreach (int n in g.Nodes) Console.WriteLine("   " + index.GetFullPath(n));
        }
        return 0;
    }

    /// <summary><c>boyut diff eski.boyut yeni.boyut</c> or <c>boyut diff C:\</c> (last two scans in the history).</summary>
    private static int Diff(Args a)
    {
        FileIndex older, newer;
        if (a.Positional.Count >= 2)
        {
            older = SnapshotSerializer.Load(a.Positional[0]);
            newer = SnapshotSerializer.Load(a.Positional[1]);
        }
        else if (a.Positional.Count == 1)
        {
            var scans = new SnapshotHistory(SnapshotHistory.DefaultFolder).List(a.Positional[0]);
            if (scans.Count < 2) return Usage($"{a.Positional[0]} için geçmişte en az iki tarama gerekir (bulunan: {scans.Count}).");
            newer = SnapshotSerializer.Load(scans[0].File);
            older = SnapshotSerializer.Load(scans[1].File);
        }
        else return Usage("Kullanım: boyut diff eski.boyut yeni.boyut  |  boyut diff <kök>");
        if (older.Metadata.ScannedAtUtc > newer.Metadata.ScannedAtUtc) (older, newer) = (newer, older);

        var diff = new SnapshotDiff(older, newer);
        string Signed(long v) => v > 0 ? Paint("+" + SizeFormatter.Format(v), "33") : Paint(SizeFormatter.Format(v), "32");
        Console.WriteLine(Paint(newer.Metadata.RootPath, "1") + Paint(
            $"  {older.Metadata.ScannedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm} → {newer.Metadata.ScannedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}", "2"));
        Console.WriteLine($"Toplam {Signed(diff.TotalDelta)} · dosya {diff.FileCountDelta:+#,0;-#,0;0}" +
                          (diff.FreeSpaceDelta is { } free ? $" · boş alan {SizeFormatter.Format(free)}" : ""));
        var growth = diff.GrowthPath();
        if (growth.Count > 1) Console.WriteLine($"Büyümenin çoğu: {diff.FullPath(growth[^1])} ({Signed(diff.Delta(growth[^1]))})");
        Console.WriteLine();

        int depth = a.Int("--depth", 2), top = a.Int("--top", 15);
        void Print(DiffEntry e, int level)
        {
            if (level >= depth) return;
            foreach (var c in diff.Children(e).Take(top))
            {
                string tag = diff.Kind(c) switch { ChangeKind.Added => " (yeni)", ChangeKind.Removed => " (silindi)", _ => "" };
                Console.WriteLine($"{Signed(diff.Delta(c)),20}  {new string(' ', level * 2)}{diff.Name(c)}{(diff.IsDirectory(c) ? "\\" : "")}{Paint(tag, "2")}");
                if (diff.IsDirectory(c)) Print(c, level + 1);
            }
        }
        Print(SnapshotDiff.Root, 0);
        return 0;
    }

    /// <summary><c>boyut refresh tarama.boyut</c> or <c>boyut refresh C:\</c>: replays the NTFS change journal.</summary>
    private static int Refresh(Args a, CancellationToken ct)
    {
        if (a.Positional.Count < 1) return Usage("Kullanım: boyut refresh <tarama.boyut | kök>");
        if (!OperatingSystem.IsWindows()) return Usage("Değişiklik günlüğü yalnızca Windows/NTFS'te var.");
        string source = a.Positional[0];
        var history = new SnapshotHistory(SnapshotHistory.DefaultFolder);
        bool fromHistory = !source.EndsWith(SnapshotSerializer.FileExtension, StringComparison.OrdinalIgnoreCase);
        string file = fromHistory
            ? history.List(source).FirstOrDefault()?.File ?? throw new ArgumentException($"{source} için geçmişte tarama yok.")
            : source;

        var old = SnapshotSerializer.Load(file);
        var options = new ScanOptions { IncludeHidden = !a.Flag("--no-hidden") };
        var updated = UsnRefresher.TryRefresh(old, options, ct);
        if (updated is null)
        {
            Console.WriteLine("Değişiklik günlüğü bu aralığı kapsamıyor (konum kaydı yok, günlük yenilenmiş ya da taşmış); tam tarama gerekir.");
            return 3;
        }

        var diff = new SnapshotDiff(old, updated);
        Console.WriteLine($"{updated.Metadata.RootPath}: {SizeFormatter.FormatDuration(updated.Metadata.Duration)} içinde güncellendi · " +
                          $"toplam {SizeFormatter.Format(updated.TotalSize)} ({(diff.TotalDelta >= 0 ? "+" : "")}{SizeFormatter.Format(diff.TotalDelta)}) · " +
                          $"{SizeFormatter.FormatCount(updated.TotalFiles)} dosya");
        if (fromHistory) Console.WriteLine("Geçmişe kaydedildi: " + history.Save(updated));
        else if (a.Value("--save") is { } save) { SnapshotSerializer.Save(updated, save); Console.WriteLine("Kaydedildi: " + save); }
        return 0;
    }

    // ---- shared -------------------------------------------------------------------------------

    private static async Task<FileIndex> LoadOrScan(string source, Args a, CancellationToken ct)
    {
        if (source.EndsWith(SnapshotSerializer.FileExtension, StringComparison.OrdinalIgnoreCase) && File.Exists(source))
            return SnapshotSerializer.Load(source);

        var engine = a.Value("--engine")?.ToLowerInvariant() switch
        {
            "mft" => ScanEngine.Mft,
            "managed" or "klasor" => ScanEngine.Managed,
            _ => ScanEngine.Auto,
        };
        if (engine == ScanEngine.Mft && OperatingSystem.IsWindows() && !Elevation.IsElevated)
            throw new UnauthorizedAccessException("MFT taraması yönetici izni ister. Terminali yönetici olarak açın ya da --engine managed kullanın.");

        var options = new ScanOptions
        {
            IncludeHidden = !a.Flag("--no-hidden"),
            FollowReparsePoints = a.Flag("--follow-links"),
            IncludeAlternateStreams = a.Flag("--streams"),
        };
        var scanner = ScannerFactory.Create(engine, source);

        bool live = !Console.IsErrorRedirected;
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!live) return;
            string fraction = p.Fraction is { } f ? $" %{f * 100:0}" : "";
            string line = $"\r{p.Phase ?? "Taranıyor"}{fraction} · {p.Items:N0} öğe · {SizeFormatter.Format(p.Bytes)}";
            Console.Error.Write(line.PadRight(Math.Min(Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 80, 100)));
        });

        var index = await scanner.ScanAsync(source, options, progress, ct);
        if (live) Console.Error.Write("\r" + new string(' ', Math.Min(Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 80, 100)) + "\r");
        if (scanner.Engine == "managed" && OperatingSystem.IsWindows() && ScannerFactory.IsLocalNtfs(source) && !Elevation.IsElevated)
            Console.Error.WriteLine(Paint("İpucu: yönetici terminalinde MFT ile tarama çok daha hızlıdır.", "2"));
        return index;
    }

    private static void PrintSummary(FileIndex index)
    {
        var m = index.Metadata;
        Console.WriteLine(Paint(m.RootPath, "1") + Paint($"  ({(m.Engine == "mft" ? "MFT" : "klasör taraması")}, {SizeFormatter.FormatDuration(m.Duration)})", "2"));
        Console.WriteLine($"{SizeFormatter.Format(index.TotalSize)} · diskte {SizeFormatter.Format(index.TotalAllocated)} · {SizeFormatter.FormatCount(index.TotalFiles)} dosya · {SizeFormatter.FormatCount(index.Count)} öğe");
        if (m.VolumeTotalBytes is { } total && m.VolumeFreeBytes is { } free)
            Console.WriteLine($"Birim: {SizeFormatter.Format(total)} · boş {SizeFormatter.Format(free)} ({SizeFormatter.FormatPercent((double)free / total)})");
        if (m.ErrorCount > 0)
            Console.WriteLine(Paint($"{m.ErrorCount} klasör okunamadı (erişim engellendi).", "33"));
        Console.WriteLine();
    }

    private static void PrintTree(FileIndex index, int node, int depth, int top, int level = 0)
    {
        if (level >= depth) return;
        long parentSize = Math.Max(1, index.Size(node));
        var children = index.Children(node);
        int shown = 0;
        foreach (int child in children)
        {
            if (shown++ >= top) break;
            double f = (double)index.EffectiveSize(child) / parentSize;
            string indent = new(' ', level * 2);
            string name = index.GetName(child) + (index.IsDirectory(child) ? Path.DirectorySeparatorChar.ToString() : "");
            Console.WriteLine($"{SizeFormatter.Format(index.Size(child)),10}  {Bar(f, 20)} {SizeFormatter.FormatPercent(f),7}  {SizeFormatter.FormatCount(index.FileCount(child)),10}  {indent}{(index.IsDirectory(child) ? Paint(name, "1") : name)}");
            if (index.IsDirectory(child)) PrintTree(index, child, depth, top, level + 1);
        }
        if (children.Length > top)
        {
            long rest = 0;
            for (int i = top; i < children.Length; i++) rest += index.EffectiveSize(children[i]);
            Console.WriteLine(Paint($"{SizeFormatter.Format(rest),10}  {new string(' ', level * 2)}… {children.Length - top} öğe daha", "2"));
        }
    }

    private static void WriteJson(FileIndex index, string path, int maxDepth)
    {
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        void Node(int n, int depth)
        {
            json.WriteStartObject();
            json.WriteString("path", index.GetFullPath(n));
            json.WriteString("name", index.GetName(n));
            json.WriteBoolean("directory", index.IsDirectory(n));
            json.WriteNumber("size", index.Size(n));
            json.WriteNumber("allocated", index.Allocated(n));
            json.WriteNumber("files", index.FileCount(n));
            if (index.ModifiedUtc(n) is { } mod) json.WriteString("modified", mod);
            if (index.IsDirectory(n) && depth < maxDepth && index.ChildCount(n) > 0)
            {
                json.WriteStartArray("children");
                foreach (int c in index.Children(n)) Node(c, depth + 1);
                json.WriteEndArray();
            }
            json.WriteEndObject();
        }
        Node(FileIndex.RootNode, 0);
    }

    private static string Bar(double fraction, int width)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        int full = (int)Math.Round(fraction * width);
        return Paint(new string('█', full), "36") + Paint(new string('░', width - full), "2");
    }

    private static string Paint(string text, string code) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Yardım için: boyut help");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Boyut — klasör ve disk analizi

            Komutlar
              boyut scan <klasör>                En büyük klasörler ve dosyalar
                  --depth 2 --top 20             Ağaç derinliği ve satır sayısı
                  --engine auto|mft|managed      MFT (yönetici, NTFS) ya da klasör taraması
                  --save tarama.boyut            Taramayı kaydet (sonra kaynak olarak kullanın)
                  --csv dosya.csv  --json dosya.json
              boyut search <kaynak> <sorgu>      Desenle ara
                  --mode glob|regex|text  --case  --full-path  --limit 50  --csv sonuc.csv
              boyut types <kaynak>               Dosya türleri ve en büyük dosyalar
              boyut cleanup <kaynak>             Temizlik önerileri
              boyut dupes <kaynak> --min-size 1mb  İçeriği aynı dosyalar
              boyut diff <eski.boyut> <yeni.boyut>  İki tarama arasında ne büyüdü / silindi
              boyut diff <kök>                   Uygulamanın tarama geçmişindeki son iki tarama
                  --depth 2 --top 15
              boyut refresh <kök | tarama.boyut>  Taramayı NTFS değişiklik günlüğüyle saniyeler içinde güncelle
                  --save yeni.boyut              (kök verilirse sonuç geçmişe eklenir)

            <kaynak>: taranacak klasör ya da kaydedilmiş .boyut dosyası

            Sorgu dili
              *.mp4;*.mkv      joker, ; = veya        **\node_modules   her derinlikte
              size:>1gb        size:10mb..500mb       ext:psd,tif       type:video,görsel
              modified:2025    modified:<2024-06      age:>90d          age:<1w
              attr:hidden      in:Downloads           kind:dir          -*.log  (hariç tut)
            """);
    }

    private sealed class Args
    {
        private readonly Dictionary<string, string?> named = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positional { get; } = [];

        public Args(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
                {
                    bool takesValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && !IsFlag(arg);
                    named[arg] = takesValue ? args[++i] : null;
                }
                else Positional.Add(arg);
            }
        }

        private static bool IsFlag(string arg) => arg is "--case" or "--full-path" or "--no-hidden" or "--follow-links"
            or "--streams" or "--files-only" or "--dirs-only";

        public bool Flag(string name) => named.ContainsKey(name);
        public string? Value(string name) => named.TryGetValue(name, out var v) ? v : null;
        public int Int(string name, int fallback) =>
            Value(name) is { } v && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;
    }
}
