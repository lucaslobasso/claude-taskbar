// Session model: merges ~/.claude/sessions/<pid>.json (live processes) with ~/.claude/jobs/<id>/state.json
// (job state, survives exit). Read-only, local files only.
using System.Text;

namespace ClaudeTaskbar;

enum Status { NeedsInput, PlanReady, Failed, Working, Done, Idle } // display order, starting at the strip's anchored edge

static class StatusInfo
{
    public static string Label(Status s) => s switch
    {
        Status.NeedsInput => "Needs input", Status.PlanReady => "Plan ready", Status.Failed => "Failed", Status.Working => "Working",
        Status.Done => "Done", _ => "Idle",
    };
    public static string Key(Status s) => s switch
    {
        Status.NeedsInput => "needsInput", Status.PlanReady => "planReady", Status.Failed => "failed", Status.Working => "working",
        Status.Done => "done", _ => "idle",
    };
    public static Status? FromKey(string k) => k switch
    {
        "needsInput" => Status.NeedsInput, "planReady" => Status.PlanReady, "failed" => Status.Failed, "working" => Status.Working,
        "done" => Status.Done, "idle" => Status.Idle, _ => null,
    };
    // 0xRRGGBB. Idle follows the theme: a light disc on a dark taskbar, a dark one on a light taskbar.
    public static uint Color(Status s, bool light) => s switch
    {
        Status.NeedsInput => 0xF0883E, Status.PlanReady => 0xA371F7, Status.Failed => 0xE5534B, Status.Working => 0x4C8DF6,
        Status.Done => 0x4FAE5A, _ => light ? 0x1A1A1Au : 0xF2F2F2u,
    };
    /// <summary>Color of the count drawn inside the dot.</summary>
    public static uint TextColor(Status s, bool light) => s == Status.Idle && !light ? 0x1A1A1Au : 0xFFFFFFu;

    /// <summary>A blocked job waiting on ExitPlanMode: Claude Code writes "approve plan" as its detail/needs.</summary>
    public static bool IsPlanApproval(string? s) => s != null && s.StartsWith("approve plan", StringComparison.OrdinalIgnoreCase);

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
    public Status Status = Status.Idle;
    public string Detail = "";
    public string? JobDir;
}

readonly record struct Change(string Name, Status To, string Detail);

static class Sessions
{
    public static List<Session> Items = [];
    /// <summary>True while any Claude session process is alive (daemon-owned and spare/pre-warmed ones included).</summary>
    public static bool SessionsAlive;
    /// <summary>True while Claude counts as open: see Scan. Drives the "when Claude is closed" setting.</summary>
    public static bool ClaudeOpen;
    /// <summary>Waitable handles of the Claude client processes (terminals, agent view); a signal means one exited.</summary>
    public static readonly List<nint> ClientHandles = [];

    sealed class Job { public DateTime Stamp; public string? State, Name, Detail, Needs, SessionId; public DateTimeOffset Updated; }
    static readonly Dictionary<string, Job> jobCache = [];
    static Dictionary<string, Status>? last; // previous statuses, null before the first scan

    /// <summary>Rescans both folders. Returns true if anything visible changed; status changes go to <paramref name="changes"/>.</summary>
    public static bool Scan(List<Change> changes)
    {
        var items = new Dictionary<string, Session>();
        bool running = false, interactive = false;

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
                                Stamp = t, State = Json.Str(r, "state"), Name = Json.Str(r, "name"), Detail = Json.Str(r, "detail"), Needs = Json.Str(r, "needs"),
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
                    if (items[id].Status == Status.NeedsInput && (StatusInfo.IsPlanApproval(job.Needs) || StatusInfo.IsPlanApproval(job.Detail)))
                        items[id].Status = Status.PlanReady;
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
                    if (Json.Str(r, "kind") is not "bg") interactive = true; // a session in a terminal of its own, not a daemon-owned background one

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
                        "waiting" => s.Status == Status.PlanReady ? Status.PlanReady : Status.NeedsInput,
                        _ => s.Status is Status.Done or Status.Failed or Status.NeedsInput or Status.PlanReady ? s.Status : Status.Idle, // idle: keep a finished job's result
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
            if (s.Status is Status.Working or Status.NeedsInput or Status.PlanReady) s.Status = Status.Idle;
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

        // "open" = a terminal/agent view is up, or something still needs attention or is running (background jobs
        // outlive their terminals). Finished/idle results alone do not keep the strip in its open state.
        bool clients = RefreshClients(); // always: it also drops the handles of exited clients
        bool open = clients || interactive || list.Any(s => s.Status is Status.Working or Status.NeedsInput or Status.PlanReady);
        bool changed = running != SessionsAlive || open != ClaudeOpen || list.Count != Items.Count ||
                       list.Zip(Items).Any(p => p.First.Key != p.Second.Key || p.First.Status != p.Second.Status ||
                                                p.First.Name != p.Second.Name || p.First.Detail != p.Second.Detail);
        Items = list;
        SessionsAlive = running;
        ClaudeOpen = open;
        return changed;
    }

    static readonly HashSet<Session> live = [];
    static readonly HashSet<uint> clientPids = [];

    /// <summary>
    /// Finds Claude client processes: claude.exe whose parent is not claude.exe (daemon, its pty hosts and the
    /// sessions they own all have one) and that is not itself the daemon. Keeps a SYNCHRONIZE handle per client so
    /// the main loop wakes when one exits. Returns true if any client exists. Cheap: one process snapshot.
    /// </summary>
    public static bool RefreshClients()
    {
        var snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snap == -1 || snap == 0) return ClientHandles.Count > 0;
        var claude = new List<(uint pid, uint parent)>();
        var claudePids = new HashSet<uint>();
        try
        {
            var e = new Native.PROCESSENTRY32W { dwSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<Native.PROCESSENTRY32W>() };
            for (bool ok = Native.Process32FirstW(snap, ref e); ok; ok = Native.Process32NextW(snap, ref e))
            {
                if (e.ExeName().Equals("claude.exe", StringComparison.OrdinalIgnoreCase)) { claude.Add((e.th32ProcessID, e.th32ParentProcessID)); claudePids.Add(e.th32ProcessID); }
            }
        }
        finally { Native.CloseHandle(snap); }

        var found = new HashSet<uint>();
        foreach (var (pid, parent) in claude)
            if (!claudePids.Contains(parent) && !Native.CommandLine(pid).Contains("daemon", StringComparison.Ordinal)) found.Add(pid);

        if (!found.SetEquals(clientPids))
        {
            foreach (var h in ClientHandles) Native.CloseHandle(h);
            ClientHandles.Clear(); clientPids.Clear();
            foreach (var pid in found.Take(32))
            {
                var h = Native.OpenProcess(Native.SYNCHRONIZE, 0, pid);
                if (h == 0) continue;
                ClientHandles.Add(h); clientPids.Add(pid);
            }
        }
        if (Log.Debug) Log.Write($"clients: claude.exe={claude.Count} found={found.Count} handles={clientPids.Count}");
        return clientPids.Count > 0;
    }

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
