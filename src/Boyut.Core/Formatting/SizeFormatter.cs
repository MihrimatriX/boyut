using System.Globalization;

namespace Boyut.Core.Formatting;

/// <summary>Explorer-style sizes (binary units, "434,6 GB") and size parsing for queries.</summary>
public static class SizeFormatter
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB"];

    public static CultureInfo DefaultCulture { get; set; } = CultureInfo.GetCultureInfo("tr-TR");

    public static string Format(long bytes, IFormatProvider? culture = null)
    {
        culture ??= DefaultCulture;
        if (bytes < 0) return "-" + Format(-bytes, culture);
        if (bytes < 1024) return bytes.ToString("N0", culture) + " bayt";

        double value = bytes;
        int unit = -1;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }

        // 9,4 GB · 434,6 GB · 512 MB — one decimal from GB up (disks are measured there), whole numbers above 100 below.
        bool wholeNumber = value >= 100 && unit < 2;
        string number = wholeNumber ? value.ToString("0", culture) : value.ToString("0.0", culture);
        return number + " " + Units[unit];
    }

    public static string FormatCount(long value, IFormatProvider? culture = null) =>
        value.ToString("N0", culture ?? DefaultCulture);

    /// <summary>0.467 → "%46,7" in Turkish, "46.7%" in English.</summary>
    public static string FormatPercent(double fraction, IFormatProvider? culture = null)
    {
        if (double.IsNaN(fraction) || double.IsInfinity(fraction)) fraction = 0;
        return fraction.ToString("P1", culture ?? DefaultCulture);
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1) return $"{duration.TotalMilliseconds:0} ms";
        if (duration.TotalMinutes < 1) return $"{duration.TotalSeconds:0.#} sn";
        return $"{(int)duration.TotalMinutes} dk {duration.Seconds} sn";
    }

    /// <summary>
    /// Parses "500", "500mb", "1.5 GB", "1,5g", "10k", "2tb". A bare number means megabytes,
    /// because nobody searches for files of "500 bytes".
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out long bytes)
    {
        bytes = 0;
        text = text.Trim();
        if (text.IsEmpty) return false;

        int split = 0;
        while (split < text.Length && (char.IsAsciiDigit(text[split]) || text[split] is '.' or ',')) split++;
        if (split == 0) return false;

        var numberPart = text[..split];
        var unitPart = text[split..].Trim();

        Span<char> normalized = stackalloc char[numberPart.Length];
        numberPart.CopyTo(normalized);
        normalized.Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return false;

        double multiplier;
        if (unitPart.IsEmpty) multiplier = 1L << 20;
        else
        {
            char u = char.ToLowerInvariant(unitPart[0]);
            multiplier = u switch
            {
                'b' => 1,
                'k' => 1L << 10,
                'm' => 1L << 20,
                'g' => 1L << 30,
                't' => 1L << 40,
                'p' => 1L << 50,
                _ => double.NaN,
            };
            if (double.IsNaN(multiplier)) return false;
            // Accept "kb", "kib", "mb", "bayt", "byte" …
            if (unitPart.Length > 3 && u != 'b') return false;
        }

        double result = value * multiplier;
        if (result > long.MaxValue) return false;
        bytes = (long)Math.Round(result);
        return true;
    }
}
