using System.Globalization;
using Boyut.Core.Scanning;

namespace Boyut.Core.Ipc;

/// <summary>
/// Line protocol between the app (medium integrity) and the elevated scan helper.
/// Helper → app: <c>P|items|bytes|fraction|phase|path</c>, <c>D|</c> (done), <c>E|message</c>.
/// App → helper: <c>C</c> (cancel). Running the scanner in a separate elevated process keeps the
/// UI unelevated, so drag and drop from Explorer keeps working (UIPI blocks it into admin windows).
/// </summary>
public static class HelperProtocol
{
    public const string Verb = "--scan-helper";

    public static string[] BuildArguments(string root, string outputFile, string pipeName, ScanOptions options) =>
        [Verb, "--root", root, "--out", outputFile, "--pipe", pipeName, "--options", options.ToBits().ToString(CultureInfo.InvariantCulture)];

    /// <summary>Quotes arguments for ProcessStartInfo.Arguments (needed with UseShellExecute / runas).</summary>
    public static string JoinForCommandLine(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        // Windows rules: backslashes are literal unless they precede a quote.
        var sb = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public sealed record HelperArguments(string Root, string OutputFile, string PipeName, ScanOptions Options);

    public static HelperArguments? TryParse(IReadOnlyList<string> args)
    {
        string? root = null, output = null, pipe = null;
        int bits = new ScanOptions().ToBits();
        for (int i = 0; i < args.Count - 1; i++)
        {
            switch (args[i])
            {
                case "--root": root = args[++i]; break;
                case "--out": output = args[++i]; break;
                case "--pipe": pipe = args[++i]; break;
                case "--options": int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out bits); break;
            }
        }
        return root is null || output is null || pipe is null ? null : new HelperArguments(root, output, pipe, ScanOptions.FromBits(bits));
    }

    public static string Progress(ScanProgress p) =>
        string.Join('|', "P",
            p.Items.ToString(CultureInfo.InvariantCulture),
            p.Bytes.ToString(CultureInfo.InvariantCulture),
            (p.Fraction ?? -1).ToString("0.####", CultureInfo.InvariantCulture),
            Clean(p.Phase),
            Clean(p.CurrentPath));

    public const string Done = "D|";
    public static string Error(string message) => "E|" + Clean(message);
    public const string Cancel = "C";

    public enum MessageKind { Progress, Done, Error, Unknown }

    public static (MessageKind Kind, ScanProgress Progress, string? Error) Parse(string line)
    {
        if (line.StartsWith("D|", StringComparison.Ordinal)) return (MessageKind.Done, default, null);
        if (line.StartsWith("E|", StringComparison.Ordinal)) return (MessageKind.Error, default, line[2..]);
        if (!line.StartsWith("P|", StringComparison.Ordinal)) return (MessageKind.Unknown, default, null);

        var parts = line.Split('|', 6);
        if (parts.Length < 6) return (MessageKind.Unknown, default, null);
        long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long items);
        long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes);
        double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction);
        return (MessageKind.Progress,
            new ScanProgress(items, bytes, parts[5].Length == 0 ? null : parts[5], fraction < 0 ? null : fraction, parts[4].Length == 0 ? null : parts[4]),
            null);
    }

    private static string Clean(string? s) => s is null ? "" : s.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '¦');
}
