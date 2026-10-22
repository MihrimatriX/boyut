using Boyut.Core.Formatting;
using Boyut.Core.Model;

namespace Boyut.Core.Analysis;

public sealed record SpaceAlert(string Title, string Message);

public sealed record AlertThresholds(long MinFreeBytes, long GrowthBytes);

/// <summary>Decides whether a background scan is worth a notification.</summary>
public static class SpaceAlerts
{
    /// <param name="diff">Previous scan → this one; null for the first scan of the root.</param>
    public static SpaceAlert? Evaluate(FileIndex current, SnapshotDiff? diff, AlertThresholds thresholds)
    {
        string root = current.Metadata.RootPath;
        string where = diff is null ? "" : WhereItGrew(diff);

        // Low free space: only when it crosses the line, so an hourly scan does not nag all day.
        if (current.Metadata.VolumeFreeBytes is { } free && free < thresholds.MinFreeBytes)
        {
            long? before = diff?.Old.Metadata.VolumeFreeBytes;
            if (before is null || before >= thresholds.MinFreeBytes)
                return new SpaceAlert($"{root} diskinde yer azaldı", $"Boş alan {SizeFormatter.Format(free)}." + where);
        }

        if (diff is not null && thresholds.GrowthBytes > 0 && diff.TotalDelta >= thresholds.GrowthBytes)
            return new SpaceAlert($"{root} son taramadan beri {SizeFormatter.Format(diff.TotalDelta)} büyüdü", where.TrimStart());

        return null;
    }

    private static string WhereItGrew(SnapshotDiff diff)
    {
        var path = diff.GrowthPath();
        if (path.Count < 2) return "";
        var top = path[^1];
        return $" En çok: {diff.FullPath(top)} (+{SizeFormatter.Format(diff.Delta(top))})";
    }
}
