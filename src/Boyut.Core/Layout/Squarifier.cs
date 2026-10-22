namespace Boyut.Core.Layout;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
}

/// <summary>
/// Squarified treemap (Bruls, Huizing, van Wijk 2000): lays values out as rectangles whose
/// aspect ratios stay close to 1, so small items remain clickable.
/// </summary>
public static class Squarifier
{
    /// <param name="values">Sizes, ideally sorted largest first. Zero/negative values get an empty rectangle.</param>
    public static TreemapRect[] Layout(ReadOnlySpan<double> values, double x, double y, double width, double height)
    {
        var result = new TreemapRect[values.Length];
        if (values.Length == 0 || width <= 0 || height <= 0) return result;

        double total = 0;
        foreach (double v in values) if (v > 0) total += v;
        if (total <= 0) return result;

        // Areas scaled to the available space; positive values only, keeping their original slots.
        var order = new List<int>(values.Length);
        var area = new double[values.Length];
        double scale = width * height / total;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] <= 0) continue;
            area[i] = values[i] * scale;
            order.Add(i);
        }
        order.Sort((a, b) => area[b].CompareTo(area[a]));

        double cx = x, cy = y, cw = width, ch = height;
        int start = 0;
        while (start < order.Count)
        {
            double side = Math.Min(cw, ch);
            int end = start;
            double rowSum = 0;
            double worst = double.MaxValue;

            // Grow the row while the worst aspect ratio improves.
            while (end < order.Count)
            {
                double candidateSum = rowSum + area[order[end]];
                double candidateWorst = Worst(area, order, start, end + 1, candidateSum, side);
                if (end > start && candidateWorst > worst) break;
                rowSum = candidateSum;
                worst = candidateWorst;
                end++;
            }

            // Lay the row along the shorter side.
            if (cw >= ch)
            {
                double rowWidth = rowSum / ch;
                double py = cy;
                for (int k = start; k < end; k++)
                {
                    double h = area[order[k]] / rowWidth;
                    result[order[k]] = new TreemapRect(cx, py, rowWidth, h);
                    py += h;
                }
                cx += rowWidth;
                cw -= rowWidth;
            }
            else
            {
                double rowHeight = rowSum / cw;
                double px = cx;
                for (int k = start; k < end; k++)
                {
                    double w = area[order[k]] / rowHeight;
                    result[order[k]] = new TreemapRect(px, cy, w, rowHeight);
                    px += w;
                }
                cy += rowHeight;
                ch -= rowHeight;
            }
            start = end;
        }
        return result;
    }

    private static double Worst(double[] area, List<int> order, int start, int end, double sum, double side)
    {
        double max = 0, min = double.MaxValue;
        for (int k = start; k < end; k++)
        {
            double a = area[order[k]];
            if (a > max) max = a;
            if (a < min) min = a;
        }
        double s2 = side * side, sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }
}
