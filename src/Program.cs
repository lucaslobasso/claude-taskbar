// Claude Taskbar: session status dots + usage gauge docked in the Windows 11 taskbar.
// One thread, event-driven: it sleeps in MsgWaitForMultipleObjects until a watched folder changes, the mouse
// touches the strip, or a one-shot timer fires. Reads local files only: no network, no tokens.
using static ClaudeTaskbar.Native;

namespace ClaudeTaskbar;

static unsafe class Program
{
    public const nuint T_DEBOUNCE = 1, T_SAFETY = 2, T_RESET = 3, T_HOVER = 4, T_TIP = 5, T_REDOCK = 6, T_NOTIFY = 7, T_TRIM = 8;
    const uint SafetyMs = 60_000;

    static readonly List<nint> watches = [];
    static bool watchingClaudeRoot;

    static int Main()
    {
        CreateMutexW(0, 0, @"Local\ClaudeTaskbar.Singleton");
        if (GetLastError() == 183 /* ERROR_ALREADY_EXISTS */) return 0;
        SetProcessDpiAwarenessContext(-4); // per-monitor v2 (also in the manifest)
        CoInitializeEx(0, 0x2);            // STA, for the window property store lookup

        Settings.Load();
        Usage.Load();
        Sessions.Scan([]);                 // first scan: never notifies
        Strip.Create();
        Strip.Fullscreen = Strip.IsFullscreen();
        Strip.Refresh(force: true);
        Log.Write($"start: hwnd={Strip.Hwnd} sessions={Sessions.Items.Count} running={Sessions.ClaudeRunning} usage={Usage.HasData} fullscreen={Strip.Fullscreen}");
        ScheduleReset();
        UpdateSafetyTimer();
        ArmWatches();
        ScheduleTrim();
        if (Environment.GetEnvironmentVariable("CLAUDE_TASKBAR_DEBUG_TIP") is { Length: > 0 } tip) Strip.DebugTip(tip);

        var handles = new nint[8];
        while (true)
        {
            int n = watches.Count;
            for (int i = 0; i < n; i++) handles[i] = watches[i];
            uint r;
            fixed (nint* hs = handles) r = MsgWaitForMultipleObjectsEx((uint)n, hs, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);

            if (r < (uint)n)
            {
                if (FindNextChangeNotification(handles[r]) == 0) ArmWatches(); // folder vanished: re-arm
                SetTimer(Strip.Hwnd, T_DEBOUNCE, 300, 0);
            }
            else if (r == (uint)n)
            {
                while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE) != 0)
                {
                    if (msg.Message == WM_QUIT) { CloseWatches(); return 0; }
                    TranslateMessage(msg);
                    DispatchMessageW(msg);
                }
            }
            else ArmWatches(); // WAIT_FAILED: a handle went bad
        }
    }

    static void CloseWatches()
    {
        foreach (var h in watches) FindCloseChangeNotification(h);
        watches.Clear();
    }

    /// <summary>Watches sessions/, jobs/ (with subfolders) and our own folder; ~/.claude itself until those exist.</summary>
    static void ArmWatches()
    {
        CloseWatches();
        Directory.CreateDirectory(Paths.App);
        void Add(string path, bool subtree, uint filter)
        {
            if (!Directory.Exists(path)) return;
            var h = FindFirstChangeNotificationW(path, subtree ? 1 : 0, filter);
            if (h != 0 && h != -1) watches.Add(h);
        }
        const uint files = FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_LAST_WRITE;
        Add(Paths.Sessions, false, files);
        Add(Paths.Jobs, true, files | FILE_NOTIFY_CHANGE_DIR_NAME);
        Add(Paths.App, false, files);
        watchingClaudeRoot = !Directory.Exists(Paths.Sessions) || !Directory.Exists(Paths.Jobs);
        if (watchingClaudeRoot) Add(Paths.Claude, false, FILE_NOTIFY_CHANGE_DIR_NAME);
    }

    /// <summary>Reloads whatever changed and redraws if needed. Notifications only for changes seen while running.</summary>
    public static void Reload(bool force = false)
    {
        if (watchingClaudeRoot && Directory.Exists(Paths.Sessions) && Directory.Exists(Paths.Jobs)) ArmWatches();
        bool changed = Settings.Load() | Usage.Load();
        var changes = new List<Change>();
        changed |= Sessions.Scan(changes);
        Notify.Show(changes);
        if (changed || force) { Strip.Refresh(force); Tooltip.RefreshIfOpen(); }
        ScheduleReset();
        UpdateSafetyTimer();
        ScheduleTrim();
    }

    static void UpdateSafetyTimer()
    {
        // only while Claude runs: catches crashed sessions whose file stays behind, and tray moves we missed
        if (Sessions.ClaudeRunning) SetTimer(Strip.Hwnd, T_SAFETY, SafetyMs, 0);
        else KillTimer(Strip.Hwnd, T_SAFETY);
    }

    /// <summary>One-shot timer at the next usage reset, so a stale percentage is never shown after it.</summary>
    static void ScheduleReset()
    {
        if (Usage.MsToNextReset() is { } ms) SetTimer(Strip.Hwnd, T_RESET, (uint)Math.Min(ms, int.MaxValue), 0);
        else KillTimer(Strip.Hwnd, T_RESET);
    }

    /// <summary>A few seconds after activity: collect garbage and hand unused pages back to Windows.</summary>
    public static void ScheduleTrim() => SetTimer(Strip.Hwnd, T_TRIM, 3_000, 0);

    public static void OnTimer(nuint id)
    {
        switch (id)
        {
            case T_DEBOUNCE: KillTimer(Strip.Hwnd, T_DEBOUNCE); Reload(); break;
            case T_SAFETY:
                Strip.Fullscreen = Strip.IsFullscreen();
                Reload();
                Strip.Refresh();
                break;
            case T_RESET: KillTimer(Strip.Hwnd, T_RESET); Strip.Refresh(); Tooltip.RefreshIfOpen(); ScheduleReset(); break;
            case T_HOVER: Strip.ShowHoverTip(); break;
            case T_TIP: Tooltip.RefreshIfOpen(); break;
            case T_REDOCK: KillTimer(Strip.Hwnd, T_REDOCK); Strip.Refresh(); break;
            case T_NOTIFY: Notify.Remove(); break;
            case T_TRIM:
                KillTimer(Strip.Hwnd, T_TRIM);
                if (Tooltip.Visible) break;
                GC.Collect();
                SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
                break;
        }
    }
}
