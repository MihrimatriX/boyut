using System.Text.RegularExpressions;
using Boyut.Core.Model;
using Boyut.Core.Search;

namespace Boyut.Core.Analysis;

public enum CleanupSafety
{
    /// <summary>Rebuilt automatically or pure cache: safe to remove.</summary>
    Safe,
    /// <summary>Removable, but you may lose something or pay a re-download/rebuild.</summary>
    Caution,
    /// <summary>Do not delete by hand; <see cref="CleanupRule.Advice"/> says what to do instead.</summary>
    Keep,
}

[Flags]
public enum CleanupZone
{
    None = 0,
    /// <summary>Windows, Program Files, ProgramData …</summary>
    System = 1,
    /// <summary>Inside a Downloads / İndirilenler folder.</summary>
    Downloads = 2,
}

public sealed class CleanupRule
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    /// <summary>What happens after removal, or the right tool to use instead.</summary>
    public string? Advice { get; init; }
    public CleanupSafety Safety { get; init; } = CleanupSafety.Safe;
    public bool RequiresAdmin { get; init; }
    public string Group { get; init; } = "Genel";

    // --- folder matching ---
    /// <summary>Folder names that trigger the rule (case-insensitive).</summary>
    public string[] FolderNames { get; init; } = [];
    /// <summary>At least one sibling must exist (names or wildcards like *.csproj).</summary>
    public string[] SiblingAny { get; init; } = [];
    /// <summary>All of these siblings must exist.</summary>
    public string[] SiblingAll { get; init; } = [];
    /// <summary>At least one child must exist (e.g. pyvenv.cfg inside a venv).</summary>
    public string[] ChildAny { get; init; } = [];
    /// <summary>Full-path wildcards for folders, e.g. <c>**\AppData\Local\Temp</c>.</summary>
    public string[] FolderPaths { get; init; } = [];

    // --- file matching (results are grouped into one finding) ---
    public string[] FilePaths { get; init; } = [];
    public string[] FileExtensions { get; init; } = [];
    public long MinFileSize { get; init; }
    public TimeSpan? MinAge { get; init; }
    public CleanupZone RequireZone { get; init; }
    public CleanupZone ExcludeZone { get; init; }

    internal bool IsFolderRule => FolderNames.Length > 0 || FolderPaths.Length > 0;
    internal bool IsFileRule => FilePaths.Length > 0 || FileExtensions.Length > 0 || MinFileSize > 0 || MinAge.HasValue;

    private Regex[]? siblingRegex, siblingAllRegex, childRegex;
    private Glob.PathMatcher[]? folderPathMatchers, filePathMatchers;
    internal Glob.PathMatcher[] FolderPathMatchers => folderPathMatchers ??= FolderPaths.Select(g => Glob.PathMatcher.Create(g, false)).ToArray();
    internal Glob.PathMatcher[] FilePathMatchers => filePathMatchers ??= FilePaths.Select(g => Glob.PathMatcher.Create(g, false)).ToArray();
    internal Regex[] SiblingRegex => siblingRegex ??= Compile(SiblingAny, path: false);
    internal Regex[] SiblingAllRegex => siblingAllRegex ??= Compile(SiblingAll, path: false);
    internal Regex[] ChildRegex => childRegex ??= Compile(ChildAny, path: false);

    private static Regex[] Compile(string[] globs, bool path) =>
        globs.Select(g => Glob.Compile(g, path, caseSensitive: false)).ToArray();
}

public sealed record CleanupFinding(CleanupRule Rule, int[] Nodes, long Bytes, long Allocated, int Files);

public static class CleanupAnalyzer
{
    private static readonly string[] SystemRoots = ["Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information", "Recovery", "$WinREAgent"];
    private static readonly string[] DownloadNames = ["Downloads", "İndirilenler", "Indirilenler"];

    /// <summary>Walks the tree once and applies every rule. A folder claimed by a rule is not descended into.</summary>
    public static IReadOnlyList<CleanupFinding> Analyze(FileIndex index, IReadOnlyList<CleanupRule>? rules = null,
        DateTime? nowUtc = null, int scope = FileIndex.RootNode, CancellationToken cancellationToken = default)
    {
        rules ??= CleanupRules.Default;
        var now = nowUtc ?? DateTime.UtcNow;
        var folderRules = rules.Where(r => r.IsFolderRule).ToArray();
        var fileRules = rules.Where(r => !r.IsFolderRule && r.IsFileRule).ToArray();
        var hits = rules.ToDictionary(r => r, _ => new List<int>());
        var path = new PathBuffer();
        bool rootIsVolume = IsVolumeRoot(index.Metadata.RootPath);

        var stack = new Stack<(int Node, CleanupZone Zone)>();
        foreach (int child in index.Children(scope)) stack.Push((child, ZoneFor(index, scope, CleanupZone.None, rootIsVolume)));

        int visited = 0;
        while (stack.Count > 0)
        {
            if ((++visited & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var (node, zone) = stack.Pop();

            if (index.IsDirectory(node))
            {
                var rule = MatchFolder(index, node, folderRules, ref path);
                if (rule is not null) { hits[rule].Add(node); continue; }
                if ((index.Flags(node) & NodeFlags.ReparsePoint) != 0) continue;

                var childZone = ZoneFor(index, node, zone, rootIsVolume);
                foreach (int child in index.Children(node)) stack.Push((child, childZone));
                continue;
            }

            foreach (var rule in fileRules)
            {
                if (MatchFile(index, node, rule, zone, now, ref path)) { hits[rule].Add(node); break; }
            }
        }

        var findings = new List<CleanupFinding>();
        foreach (var rule in rules)
        {
            var nodes = hits[rule];
            if (nodes.Count == 0) continue;
            long bytes = 0, allocated = 0;
            int files = 0;
            foreach (int n in nodes)
            {
                bytes += index.EffectiveSize(n);
                allocated += (index.Flags(n) & NodeFlags.HardLinkSecondary) != 0 ? 0 : index.Allocated(n);
                files += index.FileCount(n);
            }
            if (bytes == 0 && rule.Safety != CleanupSafety.Keep) continue;
            nodes.Sort((a, b) => index.Size(b).CompareTo(index.Size(a)));
            findings.Add(new CleanupFinding(rule, [.. nodes], bytes, allocated, files));
        }

        return findings
            .OrderBy(f => f.Rule.Safety == CleanupSafety.Keep ? 1 : 0)
            .ThenByDescending(f => f.Bytes)
            .ToList();
    }

    private static bool IsVolumeRoot(string root) =>
        root.Length <= 3 && root.Length >= 2 && root[1] == ':' || root == "/";

    private static CleanupZone ZoneFor(FileIndex index, int dir, CleanupZone inherited, bool rootIsVolume)
    {
        var zone = inherited;
        if (dir == FileIndex.RootNode) return zone;
        var name = index.NameSpan(dir);
        if (rootIsVolume && index.Parent(dir) == FileIndex.RootNode)
        {
            foreach (var s in SystemRoots)
                if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) { zone |= CleanupZone.System; break; }
        }
        foreach (var d in DownloadNames)
            if (name.Equals(d, StringComparison.OrdinalIgnoreCase)) { zone |= CleanupZone.Downloads; break; }
        return zone;
    }

    private static CleanupRule? MatchFolder(FileIndex index, int node, CleanupRule[] rules, ref PathBuffer path)
    {
        var name = index.NameSpan(node);
        foreach (var rule in rules)
        {
            bool hit = false;
            foreach (var n in rule.FolderNames)
                if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }

            if (!hit)
            {
                foreach (var m in rule.FolderPathMatchers)
                    if (m.NameMayMatch(name) && m.Path.IsMatch(path.Get(index, node))) { hit = true; break; }
            }
            if (!hit) continue;

            int parent = index.Parent(node);
            if (rule.SiblingAny.Length > 0 && !AnyChildMatches(index, parent, rule.SiblingRegex)) continue;
            if (rule.SiblingAll.Length > 0 && !rule.SiblingAllRegex.All(re => AnyChildMatches(index, parent, [re]))) continue;
            if (rule.ChildAny.Length > 0 && !AnyChildMatches(index, node, rule.ChildRegex)) continue;
            return rule;
        }
        return null;
    }

    private static bool AnyChildMatches(FileIndex index, int parent, Regex[] patterns)
    {
        if (parent < 0) return false;
        foreach (int c in index.Children(parent))
        {
            var n = index.NameSpan(c);
            foreach (var re in patterns)
                if (re.IsMatch(n)) return true;
        }
        return false;
    }

    private static bool MatchFile(FileIndex index, int node, CleanupRule rule, CleanupZone zone, DateTime now, ref PathBuffer path)
    {
        if ((zone & rule.RequireZone) != rule.RequireZone) return false;
        if ((zone & rule.ExcludeZone) != 0) return false;
        if ((index.Flags(node) & (NodeFlags.HardLinkSecondary | NodeFlags.CloudPlaceholder)) != 0) return false;
        if (index.Size(node) < rule.MinFileSize) return false;

        if (rule.MinAge is { } minAge)
        {
            long ticks = index.ModifiedTicks(node);
            if (ticks <= 0 || now - new DateTime(ticks, DateTimeKind.Utc) < minAge) return false;
        }

        if (rule.FileExtensions.Length > 0)
        {
            var ext = index.ExtensionSpan(node);
            bool any = false;
            foreach (var e in rule.FileExtensions)
                if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
            if (!any) return false;
        }

        if (rule.FilePathMatchers.Length > 0)
        {
            var name = index.NameSpan(node);
            bool any = false;
            foreach (var m in rule.FilePathMatchers)
                if (m.NameMayMatch(name) && m.Path.IsMatch(path.Get(index, node))) { any = true; break; }
            if (!any) return false;
        }
        return true;
    }
}

/// <summary>The built-in knowledge base: what common big folders are and whether they can go.</summary>
public static class CleanupRules
{
    public static IReadOnlyList<CleanupRule> Default { get; } =
    [
        // ---- Windows: leave alone, use the right tool ------------------------------------------
        new()
        {
            Id = "winsxs", Group = "Windows", Safety = CleanupSafety.Keep,
            Title = "Windows bileşen deposu (WinSxS)",
            Description = "Windows güncellemelerinin ve özelliklerinin dosyaları. Çoğu sabit bağlantı olduğu için Gezgin'de olduğundan büyük görünür.",
            Advice = "Elle silmeyin. Yönetici terminalinde: DISM /Online /Cleanup-Image /StartComponentCleanup",
            FolderPaths = [@"?:\Windows\WinSxS"],
        },
        new()
        {
            Id = "pagefile", Group = "Windows", Safety = CleanupSafety.Keep,
            Title = "Sanal bellek dosyası",
            Description = "pagefile.sys ve swapfile.sys, RAM yetmediğinde Windows'un kullandığı alandır.",
            Advice = "Boyutu Sistem › Gelişmiş sistem ayarları › Performans › Sanal bellek bölümünden değiştirilir.",
            FilePaths = [@"?:\pagefile.sys", @"?:\swapfile.sys"],
        },
        new()
        {
            Id = "hiberfil", Group = "Windows", Safety = CleanupSafety.Keep,
            Title = "Hazırda bekletme dosyası",
            Description = "hiberfil.sys, hazırda bekletme ve hızlı başlatma için RAM içeriğini tutar.",
            Advice = "Gerekmiyorsa yönetici terminalinde 'powercfg /h off' ile kaldırılır (hızlı başlatma da kapanır).",
            FilePaths = [@"?:\hiberfil.sys"],
        },
        new()
        {
            Id = "sysvol", Group = "Windows", Safety = CleanupSafety.Keep,
            Title = "Sistem Geri Yükleme noktaları",
            Description = "System Volume Information klasörü geri yükleme noktalarını ve gölge kopyaları içerir.",
            Advice = "Sistem Koruması ayarlarından (SystemPropertiesProtection) kullanım sınırını düşürün.",
            FolderPaths = [@"?:\System Volume Information"],
        },
        new()
        {
            Id = "wsl-docker", Group = "Geliştirme", Safety = CleanupSafety.Keep,
            Title = "WSL / Docker sanal diskleri",
            Description = "WSL dağıtımlarının ve Docker Desktop'ın .vhdx diskleri; içindeki veriler silinse de dosya kendiliğinden küçülmez.",
            Advice = "Silmeyin. 'wsl --manage <dağıtım> --set-sparse true' ya da Hyper-V'de Optimize-VHD ile küçültün; Docker için 'docker system prune'.",
            FileExtensions = ["vhdx"],
            FilePaths = [@"**\Docker\wsl\**", @"**\Packages\*\LocalState\*.vhdx", @"**\wsl\*.vhdx"],
        },

        // ---- Windows: safe ---------------------------------------------------------------------
        new()
        {
            Id = "recycle", Group = "Windows", Safety = CleanupSafety.Safe,
            Title = "Geri Dönüşüm Kutusu",
            Description = "Silinmiş ama henüz kalıcı olarak kaldırılmamış dosyalar.",
            Advice = "Geri Dönüşüm Kutusu'nu boşaltın.",
            FolderPaths = [@"?:\$Recycle.Bin"],
        },
        new()
        {
            Id = "user-temp", Group = "Windows", Safety = CleanupSafety.Safe,
            Title = "Geçici dosyalar (kullanıcı)",
            Description = "%TEMP% klasörü. Uygulamalar burayı ara dosyalar için kullanır.",
            Advice = "Kullanılan dosyalar atlanır; çalışan kurulumlar varsa önce bitmelerini bekleyin.",
            FolderPaths = [@"**\AppData\Local\Temp"],
        },
        new()
        {
            Id = "windows-temp", Group = "Windows", Safety = CleanupSafety.Safe, RequiresAdmin = true,
            Title = "Geçici dosyalar (Windows)",
            Description = @"C:\Windows\Temp; kurulumların ve hizmetlerin ara dosyaları.",
            FolderPaths = [@"?:\Windows\Temp"],
        },
        new()
        {
            Id = "windows-update", Group = "Windows", Safety = CleanupSafety.Safe, RequiresAdmin = true,
            Title = "Windows Update indirmeleri",
            Description = "Kurulmuş güncellemelerin indirme önbelleği.",
            Advice = "Gerekirse Windows Update yeniden indirir. Disk Temizleme › 'Sistem dosyalarını temizle' de aynı işi yapar.",
            FolderPaths = [@"?:\Windows\SoftwareDistribution\Download"],
        },
        new()
        {
            Id = "delivery-optimization", Group = "Windows", Safety = CleanupSafety.Safe, RequiresAdmin = true,
            Title = "Teslim İyileştirme önbelleği",
            Description = "Güncellemelerin diğer bilgisayarlarla paylaşılan parçaları.",
            FolderPaths = [@"?:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache", @"?:\Windows\SoftwareDistribution\DeliveryOptimization"],
        },
        new()
        {
            Id = "crash-dumps", Group = "Windows", Safety = CleanupSafety.Safe,
            Title = "Çökme dökümleri",
            Description = "Uygulama ve sistem çökmelerinden kalan bellek dökümleri.",
            FolderPaths = [@"**\AppData\Local\CrashDumps", @"?:\Windows\Minidump", @"?:\Windows\LiveKernelReports"],
        },
        new()
        {
            Id = "dump-files", Group = "Windows", Safety = CleanupSafety.Safe,
            Title = "Döküm dosyaları (*.dmp)",
            Description = "Dağınık bellek dökümü dosyaları.",
            FileExtensions = ["dmp", "mdmp", "hdmp"],
        },
        new()
        {
            Id = "windows-old", Group = "Windows", Safety = CleanupSafety.Caution, RequiresAdmin = true,
            Title = "Önceki Windows kurulumu (Windows.old)",
            Description = "Yükseltmeden kalan eski sistem; geri dönmeye yarar.",
            Advice = "Disk Temizleme › 'Önceki Windows yüklemeleri' ile kaldırın; sonra geri dönüş mümkün olmaz.",
            FolderPaths = [@"?:\Windows.old"],
        },

        // ---- Browsers & app caches ------------------------------------------------------------
        new()
        {
            Id = "browser-cache", Group = "Önbellek", Safety = CleanupSafety.Safe,
            Title = "Tarayıcı önbellekleri",
            Description = "Chrome, Edge, Brave ve Firefox'un sayfa, kod ve GPU önbellekleri. Oturum ve şifreler etkilenmez.",
            Advice = "Tarayıcıyı kapattıktan sonra temizleyin; siteler ilk açılışta biraz daha yavaş yüklenir.",
            FolderPaths =
            [
                @"**\User Data\*\Cache", @"**\User Data\*\Code Cache", @"**\User Data\*\GPUCache",
                @"**\User Data\*\Service Worker\CacheStorage", @"**\User Data\ShaderCache", @"**\User Data\GrShaderCache",
                @"**\Mozilla\Firefox\Profiles\*\cache2",
            ],
        },
        new()
        {
            Id = "electron-cache", Group = "Önbellek", Safety = CleanupSafety.Safe,
            Title = "Uygulama önbellekleri (Electron)",
            Description = "Discord, Slack, Teams, VS Code gibi uygulamaların Cache / GPUCache klasörleri.",
            FolderPaths = [@"**\AppData\Roaming\*\Cache", @"**\AppData\Roaming\*\Code Cache", @"**\AppData\Roaming\*\GPUCache", @"**\AppData\Roaming\*\CachedData"],
        },
        new()
        {
            Id = "shader-cache", Group = "Oyun", Safety = CleanupSafety.Safe,
            Title = "GPU ve oyun gölgelendirici önbellekleri",
            Description = "NVIDIA/AMD/DirectX ve Steam'in derlenmiş gölgelendiricileri.",
            Advice = "Oyunların ilk açılışı bir süre takılabilir; önbellek yeniden oluşur.",
            FolderPaths = [@"**\steamapps\shadercache", @"**\AppData\Local\NVIDIA\DXCache", @"**\AppData\Local\NVIDIA\GLCache", @"**\AppData\Local\AMD\DxCache", @"**\AppData\Local\D3DSCache"],
        },
        new()
        {
            Id = "thumbcache", Group = "Önbellek", Safety = CleanupSafety.Safe,
            Title = "Küçük resim önbelleği",
            Description = "Gezgin'in klasörlerde gösterdiği küçük resimler (thumbcache_*.db).",
            FilePaths = [@"**\AppData\Local\Microsoft\Windows\Explorer\thumbcache_*.db", @"**\AppData\Local\Microsoft\Windows\Explorer\iconcache_*.db"],
        },

        // ---- Developer: rebuildable ------------------------------------------------------------
        new()
        {
            Id = "node-modules", Group = "Geliştirme", Safety = CleanupSafety.Safe,
            Title = "Node.js bağımlılıkları (node_modules)",
            Description = "npm / pnpm / yarn paketleri. package.json yanında duran node_modules klasörleri.",
            Advice = "Projede 'npm install' (ya da pnpm/yarn) ile yeniden oluşturulur.",
            FolderNames = ["node_modules"], SiblingAny = ["package.json"],
        },
        new()
        {
            Id = "unity-library", Group = "Geliştirme", Safety = CleanupSafety.Safe,
            Title = "Unity önbelleği (Library)",
            Description = "Unity'nin içe aktarılmış varlık önbelleği; Assets ve ProjectSettings yanındaki Library klasörü.",
            Advice = "Editör kapalıyken silin. Proje açılınca yeniden içe aktarılır; ilk açılış uzun sürer.",
            FolderNames = ["Library"], SiblingAll = ["Assets", "ProjectSettings"],
        },
        new()
        {
            Id = "unity-temp", Group = "Geliştirme", Safety = CleanupSafety.Caution,
            Title = "Unity geçici ve derleme klasörleri",
            Description = "Unity projelerindeki Temp, obj ve Logs klasörleri.",
            Advice = "Editör kapalıyken silin.",
            FolderNames = ["Temp", "obj", "Logs"], SiblingAll = ["Assets", "ProjectSettings"],
        },
        new()
        {
            Id = "unreal-ddc", Group = "Geliştirme", Safety = CleanupSafety.Safe,
            Title = "Unreal önbellekleri (DerivedDataCache, Intermediate)",
            Description = ".uproject yanındaki türetilmiş veri önbelleği ve ara derleme çıktıları.",
            Advice = "Editör kapalıyken silin; açılışta gölgelendiriciler yeniden derlenir.",
            FolderNames = ["DerivedDataCache", "Intermediate"], SiblingAny = ["*.uproject", "*.uplugin"],
        },
        new()
        {
            Id = "unreal-saved", Group = "Geliştirme", Safety = CleanupSafety.Caution,
            Title = "Unreal Saved klasörü",
            Description = "Otomatik kayıtlar, loglar, çökme raporları ve yerel ayarlar.",
            Advice = "İçinde Autosaves ve Config de var; önce bakın.",
            FolderNames = ["Saved"], SiblingAny = ["*.uproject"],
        },
        new()
        {
            Id = "dotnet-build", Group = "Geliştirme", Safety = CleanupSafety.Safe,
            Title = ".NET derleme çıktıları (bin, obj)",
            Description = "Proje dosyası (.csproj, .fsproj …) yanındaki bin ve obj klasörleri.",
            Advice = "'dotnet build' ya da Visual Studio ile yeniden oluşur.",
            FolderNames = ["bin", "obj"], SiblingAny = ["*.csproj", "*.fsproj", "*.vbproj", "*.vcxproj", "*.sqlproj"],
        },
        new()
        {
            Id = "rust-target", Group = "Geliştirme", Safety = CleanupSafety.Safe,
            Title = "Rust derleme çıktıları (target)",
            Description = "Cargo.toml yanındaki target klasörü.",
            Advice = "'cargo build' ile yeniden oluşur.",
            FolderNames = ["target"], SiblingAny = ["Cargo.toml"],
        },
        new()
        {
            Id = "js-build", Group = "Geliştirme", Safety = CleanupSafety.Safe,
            Title = "Web derleme önbellekleri (.next, .nuxt, .turbo, .parcel-cache)",
            Description = "Next.js, Nuxt, Turborepo ve Parcel'in derleme önbellekleri.",
            FolderNames = [".next", ".nuxt", ".turbo", ".parcel-cache", ".svelte-kit", ".angular"], SiblingAny = ["package.json"],
        },
        new()
        {
            Id = "gradle-build", Group = "Geliştirme", Safety = CleanupSafety.Safe,
            Title = "Gradle derleme çıktıları",
            Description = "build.gradle yanındaki build ve .gradle klasörleri.",
            FolderNames = ["build", ".gradle"], SiblingAny = ["build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"],
        },
        new()
        {
            Id = "python-cache", Group = "Geliştirme", Safety = CleanupSafety.Safe,
            Title = "Python önbellekleri (__pycache__)",
            Description = "Derlenmiş .pyc dosyaları.",
            FolderNames = ["__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache"],
        },
        new()
        {
            Id = "python-venv", Group = "Geliştirme", Safety = CleanupSafety.Caution,
            Title = "Python sanal ortamları",
            Description = "pyvenv.cfg içeren .venv / venv klasörleri.",
            Advice = "requirements.txt ya da pyproject.toml ile yeniden kurulabilir.",
            FolderNames = [".venv", "venv", "env"], ChildAny = ["pyvenv.cfg"],
        },

        // ---- Package manager caches ------------------------------------------------------------
        new()
        {
            Id = "npm-cache", Group = "Paket önbelleği", Safety = CleanupSafety.Safe,
            Title = "npm / Yarn / pip önbellekleri",
            Description = "İndirilmiş paketlerin genel önbelleği.",
            Advice = "Paketler gerektiğinde yeniden indirilir. 'npm cache clean --force' da kullanılabilir.",
            FolderPaths = [@"**\AppData\Local\npm-cache", @"**\AppData\Local\Yarn\Cache", @"**\AppData\Local\pip\cache", @"**\AppData\Local\uv\cache"],
        },
        new()
        {
            Id = "pnpm-store", Group = "Paket önbelleği", Safety = CleanupSafety.Caution,
            Title = "pnpm deposu",
            Description = "pnpm'in projeler arasında paylaştığı paket deposu.",
            Advice = "'pnpm store prune' kullanılmayan paketleri güvenle temizler.",
            FolderPaths = [@"**\AppData\Local\pnpm\store", @"**\.pnpm-store"],
        },
        new()
        {
            Id = "nuget", Group = "Paket önbelleği", Safety = CleanupSafety.Caution,
            Title = "NuGet paket önbelleği",
            Description = "%USERPROFILE%\\.nuget\\packages.",
            Advice = "'dotnet nuget locals all --clear' ile temizleyin; sonraki derleme paketleri yeniden indirir.",
            FolderPaths = [@"**\.nuget\packages", @"**\AppData\Local\NuGet\v3-cache"],
        },
        new()
        {
            Id = "gradle-cache", Group = "Paket önbelleği", Safety = CleanupSafety.Caution,
            Title = "Gradle ve Maven önbellekleri",
            Description = "~/.gradle/caches ve ~/.m2/repository.",
            FolderPaths = [@"**\.gradle\caches", @"**\.m2\repository"],
        },
        new()
        {
            Id = "unity-cache", Group = "Paket önbelleği", Safety = CleanupSafety.Safe,
            Title = "Unity genel önbellekleri",
            Description = "Unity Package Manager ve Asset Store indirme önbellekleri.",
            FolderPaths = [@"**\AppData\Local\Unity\cache", @"**\AppData\Roaming\Unity\Asset Store-5.x"],
        },

        // ---- Personal files: review --------------------------------------------------------------
        new()
        {
            Id = "old-downloads", Group = "Kişisel", Safety = CleanupSafety.Caution,
            Title = "Eski kurulum ve arşiv dosyaları (İndirilenler)",
            Description = "İndirilenler klasöründe 90 günden eski kurulum, disk imajı ve arşiv dosyaları.",
            Advice = "Hâlâ gerekenleri ayırın; çoğu yeniden indirilebilir.",
            FileExtensions = ["exe", "msi", "msix", "iso", "img", "zip", "7z", "rar", "dmg", "appx"],
            RequireZone = CleanupZone.Downloads, MinAge = TimeSpan.FromDays(90),
        },
        new()
        {
            Id = "large-old", Group = "Kişisel", Safety = CleanupSafety.Caution,
            Title = "Uzun süredir değişmemiş büyük dosyalar",
            Description = "1 GB'tan büyük ve 1 yıldır değişmemiş dosyalar (sistem klasörleri hariç).",
            Advice = "Harici diske ya da buluta taşımayı düşünün.",
            MinFileSize = 1L << 30, MinAge = TimeSpan.FromDays(365), ExcludeZone = CleanupZone.System,
        },
        new()
        {
            Id = "big-logs", Group = "Kişisel", Safety = CleanupSafety.Caution,
            Title = "Büyük log dosyaları",
            Description = "50 MB'tan büyük .log dosyaları.",
            FileExtensions = ["log"], MinFileSize = 50L << 20, ExcludeZone = CleanupZone.System,
        },
    ];
}
