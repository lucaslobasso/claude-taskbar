// taskbar.json in ~/.claude/taskbar. Written with defaults on first run so it is easy to edit; re-read when it changes.
using System.Text;
using System.Text.Json;

namespace ClaudeTaskbar;

enum ClosedMode { Hide, UsageOnly, Show }

static class Paths
{
    public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string Claude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(Home, ".claude");
    public static readonly string App = Path.Combine(Claude, "taskbar");
    public static readonly string Sessions = Path.Combine(Claude, "sessions");
    public static readonly string Jobs = Path.Combine(Claude, "jobs");
    public static readonly string SettingsFile = Path.Combine(App, "taskbar.json");
    public static readonly string UsageFile = Path.Combine(App, "usage.json");
    public static readonly string LauncherExe = Path.Combine(Claude, "launcher", "ClaudeLauncher.exe");
    public static readonly string ClaudeExe = Path.Combine(Home, ".local", "bin", "claude.exe");
}

static class Settings
{
    public static ClosedMode WhenClaudeClosed = ClosedMode.Hide;
    public static bool Notifications = true;
    public static HashSet<Status> NotifyOn = [Status.NeedsInput, Status.PlanReady, Status.Done, Status.Failed];
    public static HashSet<Status> Hidden = [];
    public static int DoneWindowHours = 24;
    public static bool ShowUsage = true;
    public static bool ShowUsagePercent = true; // the percent next to each usage bar
    public static int OffsetX;      // px at 100% scaling, measured from the chosen edge
    public static bool Left;        // "position": "left" (taskbar's left edge) or "right" (next to the tray, default)
    public static string LaunchCommand = "";   // empty = Claude launcher if installed, else Windows Terminal running `claude agents`
    public static string FocusAumid = "ClaudeLauncher.WindowsTerminal";

    static DateTime stamp;

    /// <summary>Loads the file if it changed. Returns true when it did.</summary>
    public static bool Load()
    {
        try
        {
            if (!File.Exists(Paths.SettingsFile)) { Directory.CreateDirectory(Paths.App); Save(); }
            var t = File.GetLastWriteTimeUtc(Paths.SettingsFile);
            if (t == stamp) return false;
            stamp = t;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Paths.SettingsFile), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = doc.RootElement;
            WhenClaudeClosed = Str(r, "whenClaudeClosed") switch { "usageOnly" => ClosedMode.UsageOnly, "show" => ClosedMode.Show, _ => ClosedMode.Hide };
            Notifications = Bool(r, "notifications", true);
            NotifyOn = StatusSet(r, "notifyOn") ?? [Status.NeedsInput, Status.PlanReady, Status.Done, Status.Failed];
            Hidden = StatusSet(r, "hiddenStatuses") ?? [];
            DoneWindowHours = Int(r, "doneWindowHours", 24);
            ShowUsage = Bool(r, "showUsage", true);
            ShowUsagePercent = Bool(r, "showUsagePercent", true);
            OffsetX = Int(r, "offsetX", 0);
            Left = Str(r, "position") == "left";
            LaunchCommand = Str(r, "launchCommand") ?? "";
            FocusAumid = Str(r, "focusAumid") ?? "ClaudeLauncher.WindowsTerminal";
            return true;
        }
        catch { return false; } // half-written or invalid: keep the last good values
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.App);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("whenClaudeClosed", WhenClaudeClosed switch { ClosedMode.UsageOnly => "usageOnly", ClosedMode.Show => "show", _ => "hide" });
                w.WriteBoolean("notifications", Notifications);
                WriteSet(w, "notifyOn", NotifyOn);
                WriteSet(w, "hiddenStatuses", Hidden);
                w.WriteNumber("doneWindowHours", DoneWindowHours);
                w.WriteBoolean("showUsage", ShowUsage);
                w.WriteBoolean("showUsagePercent", ShowUsagePercent);
                w.WriteString("position", Left ? "left" : "right");
                w.WriteNumber("offsetX", OffsetX);
                w.WriteString("launchCommand", LaunchCommand);
                w.WriteString("focusAumid", FocusAumid);
                w.WriteEndObject();
            }
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Move(tmp, Paths.SettingsFile, true);
            stamp = File.GetLastWriteTimeUtc(Paths.SettingsFile);
        }
        catch { }
    }

    static void WriteSet(Utf8JsonWriter w, string name, HashSet<Status> set)
    {
        w.WriteStartArray(name);
        foreach (var s in Enum.GetValues<Status>()) if (set.Contains(s)) w.WriteStringValue(StatusInfo.Key(s));
        w.WriteEndArray();
    }

    static HashSet<Status>? StatusSet(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return null;
        var set = new HashSet<Status>();
        foreach (var e in a.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && StatusInfo.FromKey(e.GetString()!) is { } s) set.Add(s);
        return set;
    }

    static string? Str(JsonElement r, string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Bool(JsonElement r, string n, bool d) => r.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : d;
    static int Int(JsonElement r, string n, int d) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : d;
}

static class Json
{
    public static string? Str(JsonElement r, string n) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static double? Num(JsonElement r, string n) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    public static bool True(JsonElement r, string n) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Reads a small JSON file; null if missing, locked or half-written.</summary>
    public static JsonDocument? Read(string path)
    {
        try { return JsonDocument.Parse(File.ReadAllBytes(path)); } catch { return null; }
    }

    /// <summary>Collapses whitespace and cuts to max chars with an ellipsis.</summary>
    public static string Short(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max + 1));
        bool space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (c is '*' or '`') continue; // markdown noise
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length > max) break;
        }
        return sb.Length > max ? sb.ToString(0, max - 1).TrimEnd() + "…" : sb.ToString();
    }
}
