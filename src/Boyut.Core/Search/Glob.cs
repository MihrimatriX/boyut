using System.Text;
using System.Text.RegularExpressions;

namespace Boyut.Core.Search;

/// <summary>
/// Windows-style wildcards: <c>*</c> (within one path segment), <c>**</c> (any depth),
/// <c>?</c>, <c>[abc]</c> / <c>[!abc]</c>; <c>\</c> and <c>/</c> are interchangeable.
/// </summary>
public static class Glob
{
    public static bool HasWildcards(ReadOnlySpan<char> pattern) => pattern.IndexOfAny('*', '?', '[') >= 0;

    /// <summary>Patterns with a separator or <c>**</c> are matched against the full path.</summary>
    public static bool TargetsPath(ReadOnlySpan<char> pattern) =>
        pattern.IndexOfAny('\\', '/') >= 0 || pattern.Contains("**", StringComparison.Ordinal);

    /// <summary>
    /// Regex source for a glob. When <paramref name="pathMode"/> is true the match may start at
    /// any segment boundary (so <c>Videos\*.mp4</c> matches <c>C:\Users\Ali\Videos\a.mp4</c>).
    /// </summary>
    public static string ToRegex(string glob, bool pathMode)
    {
        var sb = new StringBuilder(glob.Length * 2 + 16);
        sb.Append(pathMode ? @"(?:^|[\\/])" : "^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] is '\\' or '/')
                        {
                            sb.Append(@"(?:.*[\\/])?"); // "**\" = zero or more whole directories
                            i++;
                        }
                        else sb.Append(".*");
                    }
                    else sb.Append(@"[^\\/]*");
                    break;
                case '?':
                    sb.Append(@"[^\\/]");
                    break;
                case '\\':
                case '/':
                    sb.Append(@"[\\/]");
                    break;
                case '[':
                    int close = glob.IndexOf(']', i + 2);
                    if (close < 0) { sb.Append(@"\["); break; }
                    var body = glob.AsSpan(i + 1, close - i - 1);
                    sb.Append('[');
                    if (body.Length > 0 && body[0] == '!') { sb.Append('^'); body = body[1..]; }
                    foreach (char ch in body) sb.Append(ch is '\\' or '[' or '^' ? "\\" + ch : ch.ToString());
                    sb.Append(']');
                    i = close;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }

    public static Regex Compile(string glob, bool pathMode, bool caseSensitive)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;
        return new Regex(ToRegex(glob, pathMode), options);
    }

    /// <summary>
    /// The last path segment of a path glob (<c>**\Cache\*.log</c> → <c>*.log</c>) when it can be
    /// checked against the bare name, or null (<c>**</c>, trailing separator). Matching the short
    /// name first lets us skip building and regex-matching the full path for almost every node.
    /// </summary>
    public static string? LastSegment(string glob)
    {
        int cut = glob.LastIndexOfAny(['\\', '/']);
        string segment = cut < 0 ? glob : glob[(cut + 1)..];
        return segment.Length == 0 || segment.Contains("**", StringComparison.Ordinal) ? null : segment;
    }

    /// <summary>A path glob plus an optional cheap name pre-check.</summary>
    public sealed record PathMatcher(string? LiteralName, Regex? Name, Regex Path, bool CaseSensitive)
    {
        public static PathMatcher Create(string glob, bool caseSensitive)
        {
            var last = LastSegment(glob);
            var path = Compile(glob, pathMode: true, caseSensitive);
            if (last is null) return new PathMatcher(null, null, path, caseSensitive);
            return HasWildcards(last)
                ? new PathMatcher(null, Compile(last, pathMode: false, caseSensitive), path, caseSensitive)
                : new PathMatcher(last, null, path, caseSensitive);
        }

        public bool NameMayMatch(ReadOnlySpan<char> name)
        {
            if (LiteralName is not null)
                return name.Equals(LiteralName, CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            return Name is null || Name.IsMatch(name);
        }
    }

    /// <summary>One-off check, handy for rules and tests.</summary>
    public static bool IsMatch(string glob, ReadOnlySpan<char> text, bool caseSensitive = false) =>
        Compile(glob, TargetsPath(glob), caseSensitive).IsMatch(text);
}
