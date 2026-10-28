using System.Globalization;
using System.Text;
using Boyut.Core.Model;

namespace Boyut.Core.Persistence;

/// <summary>
/// Excel-friendly CSV: UTF-8 with BOM, culture list separator (";" in Turkish Excel),
/// sizes as plain byte counts so they stay sortable and summable.
/// </summary>
public static class CsvExporter
{
    public static void ExportTree(FileIndex index, TextWriter writer, int startNode = FileIndex.RootNode,
        int maxDepth = int.MaxValue, bool includeFiles = true, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string sep = Separator(culture);
        WriteHeader(writer, sep);

        var stack = new Stack<(int Node, int Depth)>();
        stack.Push((startNode, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (!includeFiles && !index.IsDirectory(node)) continue;
            WriteRow(index, writer, node, depth, sep);
            if (depth >= maxDepth) continue;
            var children = index.Children(node);
            for (int i = children.Length - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
        }
    }

    public static void ExportNodes(FileIndex index, TextWriter writer, IEnumerable<int> nodes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string sep = Separator(culture);
        WriteHeader(writer, sep);
        foreach (int node in nodes) WriteRow(index, writer, node, index.Depth(node), sep);
    }

    public static void ExportToFile(string path, Action<TextWriter> write)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        write(writer);
    }

    private static string Separator(CultureInfo culture)
    {
        string s = culture.TextInfo.ListSeparator;
        return string.IsNullOrEmpty(s) ? "," : s;
    }

    private static void WriteHeader(TextWriter writer, string sep) =>
        writer.WriteLine(string.Join(sep, "Yol", "Ad", "Tür", "Boyut (bayt)", "Diskte (bayt)", "Dosya sayısı",
            "Değiştirilme (UTC)", "Öznitelikler", "Derinlik"));

    private static void WriteRow(FileIndex index, TextWriter writer, int node, int depth, string sep)
    {
        var sb = new StringBuilder(256);
        Append(sb, index.GetFullPath(node), sep).Append(sep);
        Append(sb, index.GetName(node), sep).Append(sep);
        sb.Append(index.IsDirectory(node) ? "Klasör" : "Dosya").Append(sep);
        sb.Append(index.Size(node).ToString(CultureInfo.InvariantCulture)).Append(sep);
        sb.Append(index.Allocated(node).ToString(CultureInfo.InvariantCulture)).Append(sep);
        sb.Append(index.FileCount(node).ToString(CultureInfo.InvariantCulture)).Append(sep);
        var modified = index.ModifiedUtc(node);
        sb.Append(modified?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "").Append(sep);
        Append(sb, DescribeAttributes(index, node), sep).Append(sep);
        sb.Append(depth.ToString(CultureInfo.InvariantCulture));
        writer.WriteLine(sb.ToString());
    }

    public static string DescribeAttributes(FileIndex index, int node)
    {
        var a = index.Attributes(node);
        var f = index.Flags(node);
        var parts = new List<string>(4);
        if ((a & FileAttributes.Hidden) != 0) parts.Add("gizli");
        if ((a & FileAttributes.System) != 0) parts.Add("sistem");
        if ((a & FileAttributes.ReadOnly) != 0) parts.Add("salt okunur");
        if ((a & FileAttributes.Compressed) != 0) parts.Add("sıkıştırılmış");
        if ((a & FileAttributes.SparseFile) != 0) parts.Add("seyrek");
        if ((a & FileAttributes.Encrypted) != 0) parts.Add("şifreli");
        if ((f & NodeFlags.ReparsePoint) != 0) parts.Add("bağlantı");
        if ((f & NodeFlags.CloudPlaceholder) != 0) parts.Add("bulutta");
        if ((f & NodeFlags.HardLinkSecondary) != 0) parts.Add("sabit bağlantı");
        if ((f & NodeFlags.AccessDenied) != 0) parts.Add("erişim yok");
        return string.Join(", ", parts);
    }

    private static StringBuilder Append(StringBuilder sb, string value, string sep)
    {
        bool quote = value.Contains('"') || value.Contains(sep) || value.Contains('\n') || value.Contains('\r');
        if (!quote) return sb.Append(value);
        return sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
    }
}
