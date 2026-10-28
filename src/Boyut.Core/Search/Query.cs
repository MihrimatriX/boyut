using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Boyut.Core.Analysis;
using Boyut.Core.Formatting;
using Boyut.Core.Model;

namespace Boyut.Core.Search;

public enum QueryMode
{
    /// <summary><c>*.mp4;*.mkv</c>, <c>**\node_modules</c>. Words without wildcards match anywhere in the name.</summary>
    Glob,
    /// <summary>.NET regular expression (linear-time engine, so a bad pattern cannot hang the app).</summary>
    Regex,
    /// <summary>Every word must appear in the name.</summary>
    Text,
}

public sealed record QueryOptions
{
    public bool CaseSensitive { get; init; }
    /// <summary>Match patterns against the full path instead of the name.</summary>
    public bool MatchFullPath { get; init; }
    public bool IncludeFiles { get; init; } = true;
    public bool IncludeDirectories { get; init; } = true;
}

public enum QueryTokenKind { Pattern, Size, Extension, Modified, Age, Attribute, Location, Type, Kind }

/// <summary>A parsed piece of the query, for showing "what we understood" chips in the UI.</summary>
public sealed record QueryToken(QueryTokenKind Kind, string Value, bool Negated)
{
    public string KindLabel => Kind switch
    {
        QueryTokenKind.Pattern => "desen",
        QueryTokenKind.Size => "boyut",
        QueryTokenKind.Extension => "uzantı",
        QueryTokenKind.Modified => "tarih",
        QueryTokenKind.Age => "yaş",
        QueryTokenKind.Attribute => "öznitelik",
        QueryTokenKind.Location => "konum",
        QueryTokenKind.Type => "tür",
        QueryTokenKind.Kind => "cins",
        _ => "",
    };
}

/// <summary>
/// A compiled search query. Syntax (keys also accept Turkish names):
/// <code>
/// *.mp4;*.mkv            wildcard alternatives      size:&gt;1gb  size:10mb..500mb  size:empty
/// ext:psd,tif            extensions (any)           modified:2025  modified:&lt;2025-06  modified:today
/// age:&gt;90d  age:&lt;1w      older / newer than         attr:hidden,system   (all must be set)
/// in:Downloads           path contains              type:video,image     kind:file | kind:dir
/// -*.log  -in:Windows    exclude anything           "quoted values with spaces"
/// </code>
/// </summary>
public sealed class Query
{
    private readonly Predicate[] predicates;
    private readonly bool includeFiles;
    private readonly bool includeDirectories;

    private Query(IReadOnlyList<QueryToken> tokens, Predicate[] predicates, string? error, QueryOptions options)
    {
        Tokens = tokens;
        this.predicates = predicates;
        Error = error;
        includeFiles = options.IncludeFiles;
        includeDirectories = options.IncludeDirectories;
        NeedsPath = predicates.Any(p => p.NeedsPath);
    }

    public IReadOnlyList<QueryToken> Tokens { get; }

    /// <summary>Set when the query could not be compiled (e.g. invalid regex). Such a query matches nothing.</summary>
    public string? Error { get; }

    public bool IsValid => Error is null;
    public bool IsEmpty => predicates.Length == 0;
    internal bool NeedsPath { get; }

    public bool IsMatch(FileIndex index, int node, ref PathBuffer path)
    {
        if (Error is not null) return false;
        bool isDir = index.IsDirectory(node);
        if (isDir ? !includeDirectories : !includeFiles) return false;
        path.Reset(node);
        foreach (var p in predicates)
        {
            if (p.Test(index, node, ref path) == p.Negated) return false;
        }
        return true;
    }

    public bool IsMatch(FileIndex index, int node)
    {
        var buffer = new PathBuffer();
        return IsMatch(index, node, ref buffer);
    }

    // ---- parsing -----------------------------------------------------------------------------

    public static Query Parse(string? text, QueryMode mode = QueryMode.Glob, QueryOptions? options = null, DateTime? nowUtc = null)
    {
        options ??= new QueryOptions();
        var now = nowUtc ?? DateTime.UtcNow;
        var tokens = new List<QueryToken>();
        var predicates = new List<Predicate>();
        var regexParts = new List<string>();

        try
        {
            foreach (var raw in Tokenize(text ?? string.Empty))
            {
                if (raw.Text.Length == 0) continue;
                bool negated = false;
                string token = raw.Text;
                if (!raw.Quoted && token.Length > 1 && token[0] == '-' && (mode != QueryMode.Regex || LooksLikeKey(token.AsSpan(1))))
                {
                    negated = true;
                    token = token[1..];
                }

                int colon = raw.Quoted ? -1 : token.IndexOf(':');
                if (colon > 0 && TryKey(token.AsSpan(0, colon), out var kind))
                {
                    string value = Unquote(token[(colon + 1)..]);
                    predicates.Add(BuildKeyPredicate(kind, value, now, options) with { Negated = negated });
                    tokens.Add(new QueryToken(kind, value, negated));
                    continue;
                }

                switch (mode)
                {
                    case QueryMode.Regex:
                        regexParts.Add(raw.Quoted ? Regex.Escape(raw.Text) : raw.Text);
                        break;
                    case QueryMode.Text:
                        predicates.Add(new ContainsPredicate(token, options.MatchFullPath, options.CaseSensitive) { Negated = negated });
                        tokens.Add(new QueryToken(QueryTokenKind.Pattern, token, negated));
                        break;
                    default:
                        predicates.Add(GlobPredicate.Create(token, raw.Quoted, options) with { Negated = negated });
                        tokens.Add(new QueryToken(QueryTokenKind.Pattern, token, negated));
                        break;
                }
            }

            if (regexParts.Count > 0)
            {
                string source = string.Join(' ', regexParts);
                predicates.Add(RegexPredicate.Create(source, options));
                tokens.Add(new QueryToken(QueryTokenKind.Pattern, source, false));
            }
        }
        catch (QueryException ex)
        {
            return new Query(tokens, [], ex.Message, options);
        }
        catch (ArgumentException ex)
        {
            return new Query(tokens, [], "Geçersiz regex: " + ex.Message, options);
        }

        // Cheap checks first, path work last.
        var ordered = predicates.OrderBy(p => p.Cost).ToArray();
        return new Query(tokens, ordered, null, options);
    }

    private readonly record struct RawToken(string Text, bool Quoted);

    private static IEnumerable<RawToken> Tokenize(string text)
    {
        var sb = new StringBuilder();
        bool inQuotes = false, quotedWhole = false, any = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                if (!inQuotes && sb.Length == 0) quotedWhole = true;
                inQuotes = !inQuotes;
                any = true;
                if (!quotedWhole) sb.Append(c); // keep quotes inside key:"value" for Unquote
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any || sb.Length > 0) yield return new RawToken(sb.ToString(), quotedWhole);
                sb.Clear();
                quotedWhole = false;
                any = false;
                continue;
            }
            sb.Append(c);
            any = true;
        }
        if (any || sb.Length > 0) yield return new RawToken(sb.ToString(), quotedWhole);
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value.Trim('"');

    private static bool LooksLikeKey(ReadOnlySpan<char> token)
    {
        int colon = token.IndexOf(':');
        return colon > 0 && TryKey(token[..colon], out _);
    }

    private static bool TryKey(ReadOnlySpan<char> key, out QueryTokenKind kind)
    {
        kind = FileCategories.Normalize(key) switch
        {
            "size" or "boyut" => QueryTokenKind.Size,
            "ext" or "uzanti" => QueryTokenKind.Extension,
            "modified" or "dm" or "date" or "tarih" => QueryTokenKind.Modified,
            "age" or "yas" => QueryTokenKind.Age,
            "attr" or "attrib" or "oznitelik" or "oz" => QueryTokenKind.Attribute,
            "in" or "path" or "konum" or "yol" => QueryTokenKind.Location,
            "type" or "tur" => QueryTokenKind.Type,
            "kind" or "cins" => QueryTokenKind.Kind,
            _ => (QueryTokenKind)(-1),
        };
        return (int)kind >= 0;
    }

    private static Predicate BuildKeyPredicate(QueryTokenKind kind, string value, DateTime now, QueryOptions options)
    {
        if (value.Length == 0) throw new QueryException($"'{kind.ToString().ToLowerInvariant()}:' için değer eksik.");
        return kind switch
        {
            QueryTokenKind.Size => SizePredicate.Parse(value),
            QueryTokenKind.Extension => new ExtensionPredicate(value),
            QueryTokenKind.Modified => DatePredicate.ParseModified(value, now),
            QueryTokenKind.Age => DatePredicate.ParseAge(value, now),
            QueryTokenKind.Attribute => AttributePredicate.Parse(value),
            QueryTokenKind.Location => LocationPredicate.Create(value, options.CaseSensitive),
            QueryTokenKind.Type => TypePredicate.Parse(value),
            QueryTokenKind.Kind => KindPredicate.Parse(value),
            _ => throw new QueryException("Bilinmeyen anahtar."),
        };
    }

    // ---- predicates --------------------------------------------------------------------------

    private abstract record Predicate
    {
        public bool Negated { get; init; }
        public virtual bool NeedsPath => false;
        /// <summary>Evaluation order hint: 0 = array lookup … 3 = regex over the full path.</summary>
        public virtual int Cost => 0;
        public abstract bool Test(FileIndex index, int node, ref PathBuffer path);
    }

    private sealed record ContainsPredicate(string Word, bool FullPath, bool CaseSensitive) : Predicate
    {
        public override bool NeedsPath => FullPath;
        public override int Cost => FullPath ? 3 : 1;

        public override bool Test(FileIndex index, int node, ref PathBuffer path)
        {
            var target = FullPath ? path.Get(index, node) : index.NameSpan(node);
            return target.Contains(Word, CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record GlobPredicate(Regex[] NameAlternatives, Glob.PathMatcher[] PathAlternatives, string[] Substrings, bool SubstringOnPath, bool CaseSensitive) : Predicate
    {
        public override bool NeedsPath => PathAlternatives.Length > 0 || (SubstringOnPath && Substrings.Length > 0);
        public override int Cost => NeedsPath ? 3 : 2;

        public static GlobPredicate Create(string token, bool quoted, QueryOptions options)
        {
            var names = new List<Regex>();
            var paths = new List<Glob.PathMatcher>();
            var subs = new List<string>();
            foreach (var alt in token.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                bool wildcard = !quoted && Glob.HasWildcards(alt);
                if (!wildcard) { subs.Add(alt); continue; } // plain word: "anywhere in the name"
                bool onPath = options.MatchFullPath || Glob.TargetsPath(alt);
                if (onPath) paths.Add(Glob.PathMatcher.Create(alt, options.CaseSensitive));
                else names.Add(Glob.Compile(alt, pathMode: false, options.CaseSensitive));
            }
            bool substringOnPath = options.MatchFullPath || subs.Any(s => Glob.TargetsPath(s));
            return new GlobPredicate([.. names], [.. paths], [.. subs], substringOnPath, options.CaseSensitive);
        }

        public override bool Test(FileIndex index, int node, ref PathBuffer path)
        {
            var name = index.NameSpan(node);
            foreach (var re in NameAlternatives)
                if (re.IsMatch(name)) return true;

            var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (Substrings.Length > 0)
            {
                var target = SubstringOnPath ? path.Get(index, node) : name;
                foreach (var s in Substrings)
                    if (target.Contains(s, comparison)) return true;
            }

            foreach (var matcher in PathAlternatives)
            {
                // Cheap name check first; the full path is built only for plausible candidates.
                if (!matcher.NameMayMatch(name)) continue;
                if (matcher.Path.IsMatch(path.Get(index, node))) return true;
            }
            return false;
        }
    }

    private sealed record RegexPredicate(Regex Pattern, bool FullPath) : Predicate
    {
        public override bool NeedsPath => FullPath;
        public override int Cost => 3;

        public static RegexPredicate Create(string source, QueryOptions options)
        {
            var flags = RegexOptions.CultureInvariant;
            if (!options.CaseSensitive) flags |= RegexOptions.IgnoreCase;
            Regex regex;
            try
            {
                regex = new Regex(source, flags | RegexOptions.NonBacktracking);
            }
            catch (NotSupportedException)
            {
                // Look-arounds / back-references need the backtracking engine; cap it instead.
                regex = new Regex(source, flags, TimeSpan.FromMilliseconds(50));
            }
            return new RegexPredicate(regex, options.MatchFullPath);
        }

        public override bool Test(FileIndex index, int node, ref PathBuffer path)
        {
            try
            {
                return Pattern.IsMatch(FullPath ? path.Get(index, node) : index.NameSpan(node));
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
    }

    private sealed record SizePredicate(long Min, long Max) : Predicate
    {
        public override bool Test(FileIndex index, int node, ref PathBuffer path)
        {
            long s = index.Size(node);
            return s >= Min && s <= Max;
        }

        public static SizePredicate Parse(string value)
        {
            string v = FileCategories.Normalize(value);
            if (v is "empty" or "bos" or "0") return new SizePredicate(0, 0);

            int range = value.IndexOf("..", StringComparison.Ordinal);
            if (range >= 0)
            {
                long lo = range == 0 ? 0 : ParseSize(value[..range]);
                long hi = range + 2 >= value.Length ? long.MaxValue : ParseSize(value[(range + 2)..]);
                return new SizePredicate(lo, hi);
            }

            if (value.StartsWith(">=", StringComparison.Ordinal)) return new SizePredicate(ParseSize(value[2..]), long.MaxValue);
            if (value.StartsWith("<=", StringComparison.Ordinal)) return new SizePredicate(0, ParseSize(value[2..]));
            if (value[0] == '>') return new SizePredicate(ParseSize(value[1..]) + 1, long.MaxValue);
            if (value[0] == '<') return new SizePredicate(0, Math.Max(0, ParseSize(value[1..]) - 1));
            if (value[0] == '=') { long exact = ParseSize(value[1..]); return new SizePredicate(exact, exact); }
            // A bare size means "at least": size:1gb finds everything from 1 GB up.
            return new SizePredicate(ParseSize(value), long.MaxValue);
        }

        private static long ParseSize(string text) =>
            SizeFormatter.TryParse(text, out long bytes) ? bytes : throw new QueryException($"Boyut anlaşılamadı: '{text}' (ör. 500mb, 1,5gb).");
    }

    private sealed record ExtensionPredicate : Predicate
    {
        private readonly string[] extensions;

        public ExtensionPredicate(string value)
        {
            extensions = value.Split([',', '|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.TrimStart('*').TrimStart('.'))
                .Where(e => e.Length > 0)
                .ToArray();
            if (extensions.Length == 0) throw new QueryException("Uzantı listesi boş.");
        }

        public override bool Test(FileIndex index, int node, ref PathBuffer path)
        {
            var ext = index.ExtensionSpan(node);
            if (ext.IsEmpty) return false;
            foreach (var e in extensions)
                if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    private sealed record DatePredicate(long FromTicks, long ToTicks) : Predicate
    {
        public override bool Test(FileIndex index, int node, ref PathBuffer path)
        {
            long t = index.ModifiedTicks(node);
            return t > 0 && t >= FromTicks && t < ToTicks;
        }

        /// <summary>modified:2025 · modified:2025-03 · modified:14.03.2025 · modified:&gt;2025 · modified:2024..2025-06 · modified:today</summary>
        public static DatePredicate ParseModified(string value, DateTime nowUtc)
        {
            int range = value.IndexOf("..", StringComparison.Ordinal);
            if (range >= 0)
            {
                long from = range == 0 ? 0 : Period(value[..range], nowUtc).Start;
                long to = range + 2 >= value.Length ? long.MaxValue : Period(value[(range + 2)..], nowUtc).End;
                return new DatePredicate(from, to);
            }
            if (value.StartsWith(">=", StringComparison.Ordinal)) return new DatePredicate(Period(value[2..], nowUtc).Start, long.MaxValue);
            if (value.StartsWith("<=", StringComparison.Ordinal)) return new DatePredicate(0, Period(value[2..], nowUtc).End);
            if (value[0] == '>') return new DatePredicate(Period(value[1..], nowUtc).End, long.MaxValue);
            if (value[0] == '<') return new DatePredicate(0, Period(value[1..], nowUtc).Start);
            var p = Period(value, nowUtc);
            return new DatePredicate(p.Start, p.End);
        }

        /// <summary>age:&gt;90d = older than 90 days · age:&lt;1w = changed within a week. Units: d/g, w, m/a, y.</summary>
        public static DatePredicate ParseAge(string value, DateTime nowUtc)
        {
            bool older;
            string rest;
            if (value.StartsWith(">=", StringComparison.Ordinal)) { older = true; rest = value[2..]; }
            else if (value.StartsWith("<=", StringComparison.Ordinal)) { older = false; rest = value[2..]; }
            else if (value[0] == '>') { older = true; rest = value[1..]; }
            else if (value[0] == '<') { older = false; rest = value[1..]; }
            else { older = true; rest = value; }

            var span = ParseSpan(rest);
            long boundary = (nowUtc - span).Ticks;
            return older ? new DatePredicate(1, boundary) : new DatePredicate(boundary, long.MaxValue);
        }

        private static TimeSpan ParseSpan(string text)
        {
            text = text.Trim();
            int split = 0;
            while (split < text.Length && char.IsAsciiDigit(text[split])) split++;
            if (split == 0 || !int.TryParse(text.AsSpan(0, split), out int n))
                throw new QueryException($"Süre anlaşılamadı: '{text}' (ör. 30d, 6m, 1y).");
            string unit = FileCategories.Normalize(text.AsSpan(split));
            return unit switch
            {
                "" or "d" or "g" or "gun" or "day" or "days" => TimeSpan.FromDays(n),
                "w" or "hf" or "hafta" or "week" or "weeks" => TimeSpan.FromDays(7 * n),
                "m" or "a" or "ay" or "month" or "months" => TimeSpan.FromDays(30 * n),
                "y" or "yil" or "year" or "years" => TimeSpan.FromDays(365 * n),
                "h" or "sa" or "saat" or "hour" or "hours" => TimeSpan.FromHours(n),
                _ => throw new QueryException($"Bilinmeyen süre birimi: '{unit}' (d, w, m, y)."),
            };
        }

        private static (long Start, long End) Period(string text, DateTime nowUtc)
        {
            text = text.Trim();
            var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local).Date;
            string n = FileCategories.Normalize(text);
            DateTime start, end;
            switch (n)
            {
                case "today" or "bugun": start = local; end = local.AddDays(1); break;
                case "yesterday" or "dun": start = local.AddDays(-1); end = local; break;
                case "thisweek" or "buhafta":
                    int offset = ((int)local.DayOfWeek + 6) % 7; // Monday first
                    start = local.AddDays(-offset); end = start.AddDays(7); break;
                case "thismonth" or "buay": start = new DateTime(local.Year, local.Month, 1); end = start.AddMonths(1); break;
                case "thisyear" or "buyil": start = new DateTime(local.Year, 1, 1); end = start.AddYears(1); break;
                default:
                    var inv = CultureInfo.InvariantCulture;
                    if (DateTime.TryParseExact(text, "yyyy", inv, DateTimeStyles.None, out start)) end = start.AddYears(1);
                    else if (DateTime.TryParseExact(text, ["yyyy-MM", "MM.yyyy", "yyyy/MM"], inv, DateTimeStyles.None, out start)) end = start.AddMonths(1);
                    else if (DateTime.TryParseExact(text, ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "yyyy/MM/dd"], inv, DateTimeStyles.None, out start)) end = start.AddDays(1);
                    else throw new QueryException($"Tarih anlaşılamadı: '{text}' (ör. 2025, 2025-03, 14.03.2025, bugün).");
                    break;
            }
            return (ToUtcTicks(start), ToUtcTicks(end));
        }

        private static long ToUtcTicks(DateTime local) =>
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local).Ticks;
    }

    private sealed record AttributePredicate(int Attributes, NodeFlags Flags) : Predicate
    {
        public override bool Test(FileIndex index, int node, ref PathBuffer path) =>
            (index.RawAttributes(node) & Attributes) == Attributes && (index.Flags(node) & Flags) == Flags;

        public static AttributePredicate Parse(string value)
        {
            int attrs = 0;
            var flags = NodeFlags.None;
            foreach (var part in value.Split([',', '|', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (FileCategories.Normalize(part))
                {
                    case "hidden" or "gizli" or "h": attrs |= (int)FileAttributes.Hidden; break;
                    case "system" or "sistem" or "s": attrs |= (int)FileAttributes.System; break;
                    case "readonly" or "saltokunur" or "r": attrs |= (int)FileAttributes.ReadOnly; break;
                    case "archive" or "arsiv" or "a": attrs |= (int)FileAttributes.Archive; break;
                    case "compressed" or "sikistirilmis" or "c": attrs |= (int)FileAttributes.Compressed; break;
                    case "encrypted" or "sifreli" or "e": attrs |= (int)FileAttributes.Encrypted; break;
                    case "sparse" or "seyrek": attrs |= (int)FileAttributes.SparseFile; break;
                    case "offline": attrs |= (int)FileAttributes.Offline; break;
                    case "link" or "reparse" or "baglanti" or "junction": flags |= NodeFlags.ReparsePoint; break;
                    case "cloud" or "bulut" or "bulutta": flags |= NodeFlags.CloudPlaceholder; break;
                    case "hardlink" or "sabitbaglanti": flags |= NodeFlags.HardLinkSecondary; break;
                    case "denied" or "erisimyok": flags |= NodeFlags.AccessDenied; break;
                    default: throw new QueryException($"Bilinmeyen öznitelik: '{part}' (hidden, system, readonly, compressed, link, cloud …).");
                }
            }
            return new AttributePredicate(attrs, flags);
        }
    }

    private sealed record LocationPredicate(string Text, Regex? Pattern, bool CaseSensitive) : Predicate
    {
        public override bool NeedsPath => true;
        public override int Cost => 3;

        public static LocationPredicate Create(string value, bool caseSensitive) =>
            Glob.HasWildcards(value)
                ? new LocationPredicate(value, Glob.Compile(value.TrimEnd('\\', '/') + @"\**", pathMode: true, caseSensitive), caseSensitive)
                : new LocationPredicate(value, null, caseSensitive);

        public override bool Test(FileIndex index, int node, ref PathBuffer path)
        {
            // Look at the parent's path so "in:Downloads" means "somewhere inside Downloads".
            int parent = index.Parent(node);
            if (parent < 0) return false;
            var full = path.Get(index, node);
            var parentPath = full[..Math.Max(0, full.Length - index.NameSpan(node).Length - 1)];
            if (Pattern is not null) return Pattern.IsMatch(full);
            return parentPath.Contains(Text, CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record TypePredicate(FileCategory[] Categories) : Predicate
    {
        public override int Cost => 1;

        public override bool Test(FileIndex index, int node, ref PathBuffer path) =>
            Array.IndexOf(Categories, FileCategories.Classify(index, node)) >= 0;

        public static TypePredicate Parse(string value)
        {
            var list = new List<FileCategory>();
            foreach (var part in value.Split([',', '|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!FileCategories.TryParse(part, out var c))
                    throw new QueryException($"Bilinmeyen tür: '{part}' (video, ses, görsel, belge, arşiv, kod, uygulama …).");
                list.Add(c);
            }
            return new TypePredicate([.. list]);
        }
    }

    private sealed record KindPredicate(bool Directories) : Predicate
    {
        public override bool Test(FileIndex index, int node, ref PathBuffer path) => index.IsDirectory(node) == Directories;

        public static KindPredicate Parse(string value) => FileCategories.Normalize(value) switch
        {
            "file" or "files" or "dosya" or "f" => new KindPredicate(false),
            "dir" or "dirs" or "folder" or "folders" or "klasor" or "d" => new KindPredicate(true),
            _ => throw new QueryException($"'kind:' için file veya dir yazın ('{value}' değil)."),
        };
    }
}

public sealed class QueryException(string message) : Exception(message);

/// <summary>Per-thread scratch space so a full path is built at most once per node and never allocated.</summary>
public struct PathBuffer
{
    private char[]? buffer;
    private int length;
    private int node;

    internal void Reset(int forNode)
    {
        node = -1 - forNode;
        length = 0;
    }

    public ReadOnlySpan<char> Get(FileIndex index, int forNode)
    {
        if (node == forNode && buffer is not null) return buffer.AsSpan(0, length);
        buffer ??= new char[1024];
        if (!index.TryWriteFullPath(forNode, buffer, out length))
        {
            buffer = new char[Math.Max(length, buffer.Length * 2)];
            index.TryWriteFullPath(forNode, buffer, out length);
        }
        node = forNode;
        return buffer.AsSpan(0, length);
    }
}
