// Session model: merges ~/.claude/sessions/<pid>.json (live processes) with ~/.claude/jobs/<id>/state.json
// (job state, survives exit). Read-only, local files only.
using System.Text;

namespace ClaudeTaskbar;

enum Status { Working, NeedsInput, Done, Failed, Idle } // display order

static class StatusInfo
{
    public static string Label(Status s) => s switch
    {
        Status.Working => "Working", Status.NeedsInput => "Needs input", Status.Done => "Done", Status.Failed => "Failed", _ => "Idle",
    };
    public static string Key(Status s) => s switch
    {
        Status.Working => "working", Status.NeedsInput => "needsInput", Status.Done => "done", Status.Failed => "failed", _ => "idle",
    };
    public static Status? FromKey(string k) => k switch
    {
        "working" => Status.Working, "needsInput" => Status.NeedsInput, "done" => Status.Done, "failed" => Status.Failed, "idle" => Status.Idle, _ => null,
    };
    // 0xRRGGBB
    public static uint Color(Status s) => s switch
    {
        Status.Working => 0xD97757, Status.NeedsInput => 0xE8B04B, Status.Done => 0x5FB95F, Status.Failed => 0xE5534B, _ => 0x8A8A8A,
    };

    /// <summary>Maps a job state string. Claude Code uses working/done today; the rest is matched loosely.</summary>
    public static Status? FromJobState(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        s = s.ToLowerInvariant();
        if (s.Contains("input") || s.Contains("wait") || s.Contains("block") || s.Contains("question")) return Status.NeedsInput;
        if (s.Contains("fail") || s.Contains("error") || s.Contains("crash")) return Status.Failed;
        if (s.Contains("done") || s.Contains("complete") || s.Contains("success") || s.Contains("finish")) return Status.Done;
        if (s.Contains("work") || s.Contains("run") || s.Contains("busy") || s.Contains("progress")) return Status.Working;
        if (s.Contains("idle")) return Status.Idle;
        return null;
    }
}

sealed class Session
{
    public required string Key;
    public string Name = "";
    public Status Status;
    public string Detail = "";
    public string? JobDir;
}

readonly record struct Change(string Name, Status To, string Detail);

static class Sessions
{
    public static List<Session> Items = [];
    /// <summary>True while any Claude session process is alive (spare/pre-warmed ones included).</summary>
    public static bool ClaudeRunning;

    sealed class Job { public DateTime Stamp; public string? State, Name, Detail, SessionId; public DateTimeOffset Updated; }
    static readonly Dictionary<string, Job> jobCache = [];
    static Dictionary<string, Status>? last; // previous statuses, null before the first scan

    /// <summary>Rescans both folders. Returns true if anything visible changed; status changes go to <paramref name="changes"/>.</summary>
    public static bool Scan(List<Change> changes)
    {
        var items = new Dictionary<string, Session>();
        bool running = false;

        // 1. jobs with a state.json (folders without one have been archived)
        var seenJobs = new HashSet<string>();
        try
        {
            if (Directory.Exists(Paths.Jobs))
                foreach (var dir in Directory.EnumerateDirectories(Paths.Jobs))
                {
                    var file = Path.Combine(dir, "state.json");
                    DateTime t;
                    try { t = File.GetLastWriteTimeUtc(file); } catch { continue; }
                    if (t.Year < 1700) continue; // no file
                    var id = Path.GetFileName(dir);
                    seenJobs.Add(id);
                    if (!jobCache.TryGetValue(id, out var job) || job.Stamp != t)
                    {
                        using var doc = Json.Read(file);
                        if (doc == null) { if (job == null) continue; } // half-written: keep the old copy
                        else
                        {
                            var r = doc.RootElement;
                            job = new Job
                            {
                                Stamp = t, State = Json.Str(r, "state"), Name = Json.Str(r, "name"), Detail = Json.Str(r, "detail"),
                                SessionId = Json.Str(r, "sessionId"),
                                Updated = DateTimeOffset.TryParse(Json.Str(r, "updatedAt"), out var u) ? u : new DateTimeOffset(t),
                            };
                            jobCache[id] = job;
                        }
                    }
                    items[id] = new Session
                    {
                        Key = id, JobDir = dir, Name = job!.Name ?? id, Detail = Json.Short(job.Detail, 140),
                        Status = StatusInfo.FromJobState(job.State) ?? Status.Idle,
                    };
                }
        }
        catch { }
        foreach (var k in jobCache.Keys.Where(k => !seenJobs.Contains(k)).ToList()) jobCache.Remove(k);

        // 2. live session processes override the job state
        try
        {
            if (Directory.Exists(Paths.Sessions))
                foreach (var file in Directory.EnumerateFiles(Paths.Sessions, "*.json"))
                {
                    using var doc = Json.Read(file);
                    if (doc == null) continue;
                    var r = doc.RootElement;
                    if (!IsAlive(Json.Num(r, "pid"), Json.Str(r, "procStart"))) continue;
                    running = true;
                    if (Json.True(r, "spare")) continue;

                    var key = Json.Str(r, "jobId") ?? Json.Str(r, "sessionId") ?? Path.GetFileNameWithoutExtension(file);
                    if (!items.TryGetValue(key, out var s))
                    {
                        s = new Session { Key = key, Name = Json.Str(r, "name") ?? Path.GetFileName(Json.Str(r, "cwd") ?? key) };
                        items[key] = s;
                    }
                    else if (Json.Str(r, "name") is { Length: > 0 } n) s.Name = n;
                    s.Status = Json.Str(r, "status") switch
                    {
                        "busy" => Status.Working,
                        "waiting" => Status.NeedsInput,
                        _ => s.Status is Status.Done or Status.Failed or Status.NeedsInput ? s.Status : Status.Idle, // idle: keep a finished job's result
                    };
                    // what it waits on: a specific reason beats the job's progress line; the generic one only fills a gap
                    if (s.Status == Status.NeedsInput && Json.Str(r, "waitingFor") is { Length: > 0 } wf &&
                        (s.Detail.Length == 0 || !wf.Equals("input needed", StringComparison.OrdinalIgnoreCase)))
                        s.Detail = Json.Short(wf, 140);
                    if (s.Name.Length == 0) s.Name = key;
                    live.Add(s);
                }
        }
        catch { }

        // 3. jobs without a live process: a "working" job is really stopped; old finished jobs drop off
        var cutoff = Settings.DoneWindowHours > 0 ? DateTimeOffset.UtcNow.AddHours(-Settings.DoneWindowHours) : DateTimeOffset.MinValue;
        foreach (var (key, s) in items.ToList())
        {
            if (live.Contains(s)) continue;
            if (s.Status is Status.Working or Status.NeedsInput) s.Status = Status.Idle;
            if (jobCache.TryGetValue(key, out var j) && j.Updated < cutoff) items.Remove(key);
            else if (s.Status == Status.Idle) items.Remove(key); // not running and no result to show
        }
        live.Clear();

        var list = items.Values.OrderBy(s => s.Status).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // 4. status changes (only after the first scan, so startup never notifies)
        var now = list.ToDictionary(s => s.Key, s => s.Status);
        if (last != null)
            foreach (var s in list)
                if (!last.TryGetValue(s.Key, out var was) ? s.Status != Status.Working && s.Status != Status.Idle : was != s.Status)
                    changes.Add(new Change(s.Name, s.Status, s.Detail));
        last = now;

        bool changed = running != ClaudeRunning || list.Count != Items.Count ||
                       list.Zip(Items).Any(p => p.First.Key != p.Second.Key || p.First.Status != p.Second.Status ||
                                                p.First.Name != p.Second.Name || p.First.Detail != p.Second.Detail);
        Items = list;
        ClaudeRunning = running;
        return changed;
    }

    static readonly HashSet<Session> live = [];

    /// <summary>pid alive and started at procStart (FILETIME ticks), so a recycled pid does not count.</summary>
    static bool IsAlive(double? pid, string? procStart)
    {
        if (pid is not { } p || p <= 0) return false;
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, 0, (uint)p);
        if (h == 0) return false;
        try
        {
            if (Native.GetExitCodeProcess(h, out var code) == 0 || code != 259 /* STILL_ACTIVE */) return false;
            if (procStart == null || !long.TryParse(procStart, out var want)) return true;
            return Native.GetProcessTimes(h, out var created, out _, out _, out _) != 0 && Math.Abs(created - want) < 10_000_000; // within 1 s
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>Last message for a tooltip: the job detail, else the tail of timeline.jsonl (read only on hover).</summary>
    public static string LastMessage(Session s)
    {
        if (s.Detail.Length > 0 || s.JobDir == null) return s.Detail;
        try
        {
            var file = Path.Combine(s.JobDir, "timeline.jsonl");
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = (int)Math.Min(fs.Length, 16 * 1024);
            fs.Seek(-len, SeekOrigin.End);
            var buf = new byte[len];
            fs.ReadExactly(buf);
            var lines = Encoding.UTF8.GetString(buf).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(lines[i]);
                    var d = Json.Str(doc.RootElement, "detail") ?? Json.Str(doc.RootElement, "text");
                    if (!string.IsNullOrWhiteSpace(d)) return Json.Short(d, 140);
                }
                catch { } // first line may be cut
            }
        }
        catch { }
        return "";
    }

    public static int Count(Status s) => Items.Count(i => i.Status == s);
}
