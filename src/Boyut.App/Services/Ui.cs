using System.Globalization;
using Boyut.Core.Analysis;
using Boyut.Core.Formatting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Boyut.App.Services;

/// <summary>Static helpers for x:Bind function bindings (no converters needed).</summary>
public static class Ui
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Size(long bytes) => SizeFormatter.Format(bytes, Tr);
    public static string Count(long value) => SizeFormatter.FormatCount(value, Tr);
    public static string SignedSize(long bytes) => bytes > 0 ? "+" + Size(bytes) : Size(bytes);
    public static string SignedCount(long value) => value > 0 ? "+" + Count(value) : Count(value);

    /// <summary>Growth is what the user is hunting for: warm for more, green for less.</summary>
    public static Color DeltaColor(long delta) => delta > 0 ? Hex("#D0613B") : delta < 0 ? Hex("#2E9E5B") : Hex("#8A8A8A");
    public static string Percent(double fraction) => SizeFormatter.FormatPercent(fraction, Tr);
    public static string Date(DateTime? utc) => utc is { } d ? d.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Tr) : "—";
    public static string ShortDate(DateTime? utc) => utc is { } d ? d.ToLocalTime().ToString("dd.MM.yyyy", Tr) : "—";

    public static string Relative(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "az önce";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} dk önce";
        if (span.TotalDays < 1) return $"bugün {utc.ToLocalTime():HH:mm}";
        if (span.TotalDays < 2) return $"dün {utc.ToLocalTime():HH:mm}";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} gün önce";
        return ShortDate(utc);
    }

    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility ShowIf(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility ShowIf(bool condition, string? text) => condition && !string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility ShowIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public static bool Not(bool value) => !value;
    public static bool HasText(string? text) => !string.IsNullOrEmpty(text);
    public static string DuplicateButton(bool running) => running ? "Durdur" : "Kopyaları bul";
    public static Thickness Indent(int depth) => new(depth * 16, 0, 0, 0);
    /// <summary>For a tab strip: visible when <paramref name="selected"/> is <paramref name="tab"/>.</summary>
    public static Visibility Tab(int selected, int tab) => selected == tab ? Visibility.Visible : Visibility.Collapsed;
    public static double Clamp100(double value) => Math.Clamp(value, 0, 100);

    public static SolidColorBrush Brush(Color color) => new(color);
    public static Windows.UI.Text.FontWeight Weight(bool bold) => bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    /// <summary>Pastel tile colours (dark text stays readable on all of them, in light and dark theme).</summary>
    public static readonly Color[] TilePalette =
    [
        Hex("#8AB8EE"), Hex("#F3B37C"), Hex("#8FD0AE"), Hex("#C6A9EC"), Hex("#F0A5B7"),
        Hex("#E3CA74"), Hex("#A5BCCD"), Hex("#9DD3E0"), Hex("#D9B8A0"), Hex("#B9D58A"),
    ];

    public static Color CategoryColor(FileCategory category) => category switch
    {
        FileCategory.Folder => Hex("#E3B84A"),
        FileCategory.Video => Hex("#F0A35E"),
        FileCategory.Audio => Hex("#7FC8C0"),
        FileCategory.Image => Hex("#E58BA2"),
        FileCategory.Document => Hex("#62BC8E"),
        FileCategory.Archive => Hex("#B48DE6"),
        FileCategory.DiskImage => Hex("#9A86D8"),
        FileCategory.Code => Hex("#6FA8DC"),
        FileCategory.Executable => Hex("#5B9BE6"),
        FileCategory.GameAsset => Hex("#D4A373"),
        FileCategory.System => Hex("#8FA6BA"),
        _ => Hex("#C9CED3"),
    };

    public static SolidColorBrush UsageBrush(double usedPercent) => new(UsageColor(usedPercent));

    /// <summary>How full a disk is, at a glance (drive cards, status bar): blue, amber from 80 %, red from 90 % like Explorer.</summary>
    public static Color UsageColor(double usedPercent) => usedPercent switch
    {
        >= 90 => Hex("#E5484D"),
        >= 80 => Hex("#F0A020"),
        _ => Hex("#3B82F6"),
    };

    /// <summary>TreeSize-style share bars behind tree names: one hue per level, translucent so the name stays readable in both themes.</summary>
    public static Color LevelColor(int depth)
    {
        var c = LevelPalette[Math.Max(0, depth - 1) % LevelPalette.Length];
        return Color.FromArgb(0x58, c.R, c.G, c.B);
    }

    private static readonly Color[] LevelPalette =
    [
        Hex("#3B82F6"), Hex("#10B981"), Hex("#F59E0B"), Hex("#8B5CF6"), Hex("#EC4899"), Hex("#06B6D4"),
    ];

    /// <summary>A lighter tint of a tile colour, for the second treemap level.</summary>
    public static Color Tint(Color c, double amount) => Color.FromArgb(c.A,
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    /// <summary>A darker shade of a tile colour (treemap frames).</summary>
    public static Color Shade(Color c, double factor) => Color.FromArgb(c.A, (byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

    public static Color SafetyColor(CleanupSafety safety) => safety switch
    {
        CleanupSafety.Safe => Hex("#2E9E5B"),
        CleanupSafety.Caution => Hex("#C27C0E"),
        _ => Hex("#5A7183"),
    };

    public static string SafetyLabel(CleanupSafety safety) => safety switch
    {
        CleanupSafety.Safe => "Güvenli",
        CleanupSafety.Caution => "Gözden geçir",
        _ => "Dokunma",
    };

    public static Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex[..2], 16), g = Convert.ToByte(hex[2..4], 16), b = Convert.ToByte(hex[4..6], 16);
        return Color.FromArgb(255, r, g, b);
    }

    // Segoe Fluent Icons glyphs used in templates.
    public const string GlyphFolder = "";
    public const string GlyphFile = "";
    public const string GlyphDrive = "";
    public const string GlyphLink = "";
    public const string GlyphCloud = "";
    public const string GlyphMore = "";
}

/// <summary>Star widths for a two-column "filled / empty" bar.</summary>
public static class BarWidth
{
    public static GridLength Filled(double percent) => new(Math.Max(0.0001, Math.Clamp(percent, 0, 100)), GridUnitType.Star);
    public static GridLength Empty(double percent) => new(Math.Max(0.0001, 100 - Math.Clamp(percent, 0, 100)), GridUnitType.Star);
}
