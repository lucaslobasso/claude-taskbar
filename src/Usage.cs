// usage.json is written by the boxed-messages status line (no API calls here, no tokens).
// Countdowns are computed from the stored reset dates, so they stay exact even when the snapshot is old.
namespace ClaudeTaskbar;

static class Usage
{
    public struct Window { public double? Pct; public DateTimeOffset? Reset; }
    public static Window FiveHour, Weekly;
    public static DateTimeOffset? WrittenAt;
    static DateTime stamp;

    public static bool HasData => FiveHour.Pct != null || Weekly.Pct != null;

    public static bool Load()
    {
        try
        {
            if (!File.Exists(Paths.UsageFile)) { var had = HasData; FiveHour = Weekly = default; WrittenAt = null; stamp = default; return had; }
            var t = File.GetLastWriteTimeUtc(Paths.UsageFile);
            if (t == stamp) return false;
            using var doc = Json.Read(Paths.UsageFile);
            if (doc == null) return false; // retry on the next change
            stamp = t;
            var r = doc.RootElement;
            FiveHour = Parse(r, "five_hour");
            Weekly = Parse(r, "seven_day");
            WrittenAt = Json.Num(r, "writtenAt") is { } w ? DateTimeOffset.FromUnixTimeMilliseconds((long)w) : null;
            return true;
        }
        catch { return false; }
    }

    static Window Parse(System.Text.Json.JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var w)) return default;
        return new Window
        {
            Pct = Json.Num(w, "used_percentage"),
            Reset = Json.Num(w, "resets_at") is { } s ? DateTimeOffset.FromUnixTimeSeconds((long)s) : null,
        };
    }

    /// <summary>The percentage, or null once the window has reset (the stored value would be stale).</summary>
    public static double? Current(Window w) => w.Reset is { } r && r <= DateTimeOffset.UtcNow ? null : w.Pct;

    /// <summary>Milliseconds until the next stored reset moment, or null if none is ahead.</summary>
    public static long? MsToNextReset()
    {
        var now = DateTimeOffset.UtcNow;
        long? best = null;
        foreach (var w in new[] { FiveHour, Weekly })
            if (w.Reset is { } r && r > now) { var ms = (long)(r - now).TotalMilliseconds + 500; if (best == null || ms < best) best = ms; }
        return best;
    }

    public static string Countdown(DateTimeOffset reset)
    {
        var left = reset - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return "reset";
        if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d {left.Hours}h";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes}m";
        return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
    }

    public static string Ago(DateTimeOffset t)
    {
        var a = DateTimeOffset.UtcNow - t;
        if (a.TotalMinutes < 1) return "just now";
        if (a.TotalHours < 1) return $"{(int)a.TotalMinutes} min ago";
        if (a.TotalDays < 1) return $"{(int)a.TotalHours} h ago";
        return $"{(int)a.TotalDays} d ago";
    }
}
