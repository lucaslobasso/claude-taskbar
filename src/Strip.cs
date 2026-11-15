// The strip: a per-pixel-alpha layered window owned by the taskbar (so it stays above it), placed just left of the
// tray. Drawn only when its content, position, DPI or theme changes.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ClaudeTaskbar.Native;

namespace ClaudeTaskbar;

static unsafe class Strip
{
    public static nint Hwnd;
    public static uint TaskbarCreatedMsg;
    public static bool Fullscreen;

    const int GaugeKind = 100;
    record struct Region(int X0, int X1, int Kind);
    static Region[] regions = [];

    static nint taskbar, tray, hookLoc, hookFg;
    static uint dpi = 96;
    static bool tracking, visible;
    static int hover = -1;
    static string lastSig = "";
    static bool debug => Log.Debug;
    static int stripX, stripY, stripW, stripH;

    public static void Create()
    {
        var inst = GetModuleHandleW(0);
        var cls = (char*)Marshal.StringToHGlobalUni("ClaudeTaskbarStrip");
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = inst,
            hCursor = LoadCursorW(0, 32512), lpszClassName = cls,
        };
        var atom = RegisterClassExW(&wc);
        if (atom == 0) Log.Write($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
        Hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST, "ClaudeTaskbarStrip", "Claude Taskbar", WS_POPUP, 0, 0, 1, 1, 0, 0, inst, 0);
        if (Hwnd == 0) Log.Write($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        TaskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        hookFg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, &OnWinEvent, 0, 0, WINEVENT_OUTOFCONTEXT | 0x2 /* SKIPOWNPROCESS */);
        AttachTaskbar();
    }

    /// <summary>Finds the taskbar and tray, makes the taskbar our owner and follows its tray's size changes.</summary>
    static bool AttachTaskbar()
    {
        var tb = FindWindowW("Shell_TrayWnd", null);
        if (tb == 0) { taskbar = tray = 0; return false; }
        if (tb != taskbar)
        {
            taskbar = tb;
            SetWindowLongPtrW(Hwnd, GWLP_HWNDPARENT, taskbar); // owned by the taskbar = always drawn above it
            if (hookLoc != 0) UnhookWinEvent(hookLoc);
            GetWindowThreadProcessId(taskbar, out var pid);
            hookLoc = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, 0, &OnWinEvent, pid, 0, WINEVENT_OUTOFCONTEXT);
        }
        tray = FindWindowExW(taskbar, 0, "TrayNotifyWnd", null);
        return true;
    }

    [UnmanagedCallersOnly]
    static void OnWinEvent(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (ev == EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (idObject == 0 && hwnd != 0 && (hwnd == tray || hwnd == taskbar)) SetTimer(Hwnd, Program.T_REDOCK, 120, 0);
        }
        else if (ev == EVENT_SYSTEM_FOREGROUND)
        {
            var fs = IsFullscreen();
            if (fs != Fullscreen) { Fullscreen = fs; Refresh(); }
        }
    }

    public static bool IsFullscreen() => SHQueryUserNotificationState(out var s) == 0 && s is 2 or 3 or 4; // busy, D3D fullscreen, presentation

    // ---- content -----------------------------------------------------------------------------------------------

    record struct Item(int Kind, string Text, uint Color, double Fill); // Kind: (int)Status or GaugeKind

    static List<Item> BuildItems()
    {
        var items = new List<Item>();
        bool sessions = Sessions.ClaudeRunning || Settings.WhenClaudeClosed == ClosedMode.Show;
        bool gauge = Settings.ShowUsage && Usage.HasData && (Sessions.ClaudeRunning || Settings.WhenClaudeClosed != ClosedMode.Hide);
        if (sessions)
            foreach (var s in Enum.GetValues<Status>())
            {
                if (Settings.Hidden.Contains(s)) continue;
                var n = Sessions.Count(s);
                if (n > 0) items.Add(new Item((int)s, n.ToString(), StatusInfo.Color(s), 0));
            }
        if (gauge)
        {
            // the most constraining window: whichever is closer to its limit (the tooltip lists both)
            double? p5 = Usage.Current(Usage.FiveHour), p7 = Usage.Current(Usage.Weekly);
            double? p = p5 == null ? p7 : p7 == null ? p5 : Math.Max(p5.Value, p7.Value);
            uint col = p is >= 90 ? 0xE5534Bu : p is >= 70 ? 0xE8B04Bu : 0u;
            items.Add(new Item(GaugeKind, p is { } v ? $"{Math.Round(v):0}%" : "–", col, Math.Clamp((p ?? 0) / 100.0, 0, 1)));
        }
        return items;
    }

    static bool LightTheme() => RegDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) == 1;

    /// <summary>Re-reads position and content; redraws only if something visible changed.</summary>
    public static void Refresh(bool force = false)
    {
        if (taskbar == 0 || IsWindow(taskbar) == 0 || tray == 0 || IsWindow(tray) == 0) AttachTaskbar();
        var items = BuildItems();
        if (taskbar == 0 || Fullscreen || items.Count == 0) { Hide(); return; }

        GetWindowRect(taskbar, out var tb);
        RECT tr = default;
        if (tray != 0) GetWindowRect(tray, out tr); else tr.Left = tb.Right;
        dpi = GetDpiForWindow(taskbar);
        if (dpi == 0) dpi = 96;
        bool light = LightTheme();

        var sig = $"{tb.Left},{tb.Top},{tb.Right},{tb.Bottom},{tr.Left},{dpi},{light},{Settings.OffsetX},{Settings.Left}|" +
                  string.Join(";", items.Select(i => $"{i.Kind}:{i.Text}:{i.Color}:{i.Fill:0.000}"));
        if (!force && sig == lastSig && visible) return;
        lastSig = sig;

        Render(items, tb, tr, light);
        if (!visible) { ShowWindow(Hwnd, SW_SHOWNA); visible = true; }
        SetWindowPos(Hwnd, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); // above the (topmost) taskbar
    }

    public static void Hide()
    {
        if (visible) { ShowWindow(Hwnd, SW_HIDE); visible = false; }
        lastSig = "";
        Tooltip.Hide();
    }

    // ---- drawing -----------------------------------------------------------------------------------------------

    static void Render(List<Item> items, RECT tb, RECT tr, bool light)
    {
        float s = dpi / 96f;
        int H = tb.Height;
        int pad = R(6 * s), dotD = R(8 * s), dotGap = R(5 * s), groupGap = R(11 * s), ringD = R(14 * s);

        var font = CreateFontW(-R(12 * s), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4 /* ANTIALIASED_QUALITY: grayscale coverage */, 0, "Segoe UI");
        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        ReleaseDC(0, screen);
        var oldFont = SelectObject(dc, font);

        // layout
        var widths = new int[items.Count];
        int textH = 0, W = pad;
        for (int i = 0; i < items.Count; i++)
        {
            GetTextExtentPoint32W(dc, items[i].Text, items[i].Text.Length, out var sz);
            widths[i] = sz.Cx; textH = Math.Max(textH, sz.Cy);
            W += (items[i].Kind == GaugeKind ? ringD : dotD) + dotGap + sz.Cx + (i < items.Count - 1 ? groupGap : 0);
        }
        W += pad;

        var bmi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32 };
        var bmp = CreateDIBSection(dc, &bmi, 0, out var bitsPtr, 0, 0);
        var oldBmp = SelectObject(dc, bmp);
        var px = (uint*)bitsPtr;

        // text as white-on-black coverage, then turned into premultiplied color below
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, 0xFFFFFF);
        var regs = new Region[items.Count];
        int x = pad, ty = (H - textH) / 2;
        var shapes = new List<(int cx, int d, Item item)>();
        for (int i = 0; i < items.Count; i++)
        {
            int x0 = x, d = items[i].Kind == GaugeKind ? ringD : dotD;
            shapes.Add((x + d / 2, d, items[i]));
            x += d + dotGap;
            TextOutW(dc, x, ty, items[i].Text, items[i].Text.Length);
            x += widths[i];
            int gap = i < items.Count - 1 ? groupGap : 0;
            regs[i] = new Region(x0 - (i == 0 ? pad : gap / 2), x + (i == items.Count - 1 ? pad : gap / 2), items[i].Kind);
            x += gap;
        }
        GdiFlush();

        uint fg = light ? 0x1A1A1Au : 0xFFFFFFu;
        for (int i = 0; i < W * H; i++)
        {
            uint c = px[i];
            int cov = Math.Max((int)(c & 0xFF), Math.Max((int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF)));
            px[i] = cov == 0 ? 0x01000000u : Premul(fg, cov); // alpha 1 keeps the whole strip clickable
        }

        int cy = H / 2;
        foreach (var (cx, d, item) in shapes)
        {
            if (item.Kind == GaugeKind) Ring(px, W, H, cx, cy, d / 2f, Math.Max(1.6f, 2.2f * s), item.Fill, item.Color == 0 ? fg : item.Color, fg);
            else Disc(px, W, H, cx, cy, d / 2f, item.Color);
        }

        stripW = W; stripH = H;
        // right: just left of the tray. left: from the taskbar's left edge (with centered icons that side is empty;
        // use offsetX to clear the Widgets button if it is on)
        stripX = Settings.Left ? tb.Left + R(8 * s) + R(Settings.OffsetX * s) : tr.Left - W - R(Settings.OffsetX * s) - R(2 * s);
        stripY = tb.Top;
        var dst = new POINT { X = stripX, Y = stripY };
        var size = new SIZE { Cx = W, Cy = H };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        if (UpdateLayeredWindow(Hwnd, 0, &dst, &size, dc, &src, 0, &blend, ULW_ALPHA) == 0) Log.Write($"UpdateLayeredWindow failed: {Marshal.GetLastPInvokeError()}");
        if (debug) Log.Write($"render: {W}x{H} at {stripX},{stripY} items={items.Count} taskbar={tb.Left},{tb.Top},{tb.Right},{tb.Bottom} tray={tr.Left} dpi={dpi}");

        SelectObject(dc, oldBmp); SelectObject(dc, oldFont);
        DeleteObject(bmp); DeleteObject(font); DeleteDC(dc);
        regions = regs;
    }

    static int R(float v) => (int)MathF.Round(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Premul(uint rgb, int a)
    {
        uint r = (uint)((((rgb >> 16) & 0xFF) * a + 127) / 255), g = (uint)((((rgb >> 8) & 0xFF) * a + 127) / 255), b = (uint)(((rgb & 0xFF) * a + 127) / 255);
        return ((uint)a << 24) | (r << 16) | (g << 8) | b;
    }

    static void Put(uint* px, int W, int H, int x, int y, uint rgb, float cov)
    {
        if ((uint)x >= (uint)W || (uint)y >= (uint)H || cov <= 0) return;
        int a = (int)(Math.Min(cov, 1f) * 255 + 0.5f);
        if (a > (int)(px[y * W + x] >> 24)) px[y * W + x] = Premul(rgb, a);
    }

    static void Disc(uint* px, int W, int H, int cx, int cy, float r, uint rgb)
    {
        float fx = cx, fy = cy; // integer center on the pixel grid: symmetric for even diameters
        for (int y = (int)(fy - r - 1); y <= (int)(fy + r + 1); y++)
            for (int x = (int)(fx - r - 1); x <= (int)(fx + r + 1); x++)
            {
                float dx = x + 0.5f - fx, dy = y + 0.5f - fy;
                Put(px, W, H, x, y, rgb, r + 0.5f - MathF.Sqrt(dx * dx + dy * dy));
            }
    }

    static void Ring(uint* px, int W, int H, int cx, int cy, float ro, float stroke, double fill, uint rgb, uint track)
    {
        float ri = ro - stroke, limit = (float)(fill * Math.PI * 2);
        for (int y = (int)(cy - ro - 1); y <= (int)(cy + ro + 1); y++)
            for (int x = (int)(cx - ro - 1); x <= (int)(cx + ro + 1); x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy, d = MathF.Sqrt(dx * dx + dy * dy);
                float cov = Math.Min(Math.Clamp(ro + 0.5f - d, 0, 1), Math.Clamp(d - ri + 0.5f, 0, 1));
                if (cov <= 0) continue;
                float a = MathF.Atan2(dx, -dy); if (a < 0) a += MathF.PI * 2; // 0 at 12 o'clock, clockwise
                if (a <= limit) Put(px, W, H, x, y, rgb, cov);
                else Put(px, W, H, x, y, track, cov * 0.28f);
            }
    }

    // ---- input -------------------------------------------------------------------------------------------------

    static int RegionAt(int x)
    {
        for (int i = 0; i < regions.Length; i++) if (x >= regions[i].X0 && x < regions[i].X1) return i;
        return -1;
    }

    /// <summary>Debug aid (CLAUDE_TASKBAR_DEBUG_TIP=gauge|working|needsInput|done|failed|idle): open that tooltip.</summary>
    public static void DebugTip(string key)
    {
        int kind = key == "gauge" ? GaugeKind : StatusInfo.FromKey(key) is { } s ? (int)s : -1;
        hover = Array.FindIndex(regions, r => r.Kind == kind);
        ShowHoverTip();
    }

    public static void ShowHoverTip()
    {
        KillTimer(Hwnd, Program.T_HOVER);
        if (hover < 0 || hover >= regions.Length || !visible) return;
        var r = regions[hover];
        Tooltip.Show(r.Kind == GaugeKind ? null : (Status)r.Kind, stripX + (r.X0 + r.X1) / 2, stripY, dpi);
    }

    static void Menu()
    {
        const int Open = 1, Refresh_ = 2, Notif = 10, Hide_ = 20, UsageOnly = 21, Show_ = 22, Gauge = 30, Auto = 40, Edit = 50,
            PosLeft = 60, PosRight = 61, Exit = 99;
        var pos = CreatePopupMenu();
        AppendMenuW(pos, Settings.Left ? MF_CHECKED : 0, PosLeft, "Left side of the taskbar");
        AppendMenuW(pos, Settings.Left ? 0 : MF_CHECKED, PosRight, "Next to the tray");
        var m = CreatePopupMenu();
        var sub = CreatePopupMenu();
        AppendMenuW(sub, Settings.WhenClaudeClosed == ClosedMode.Hide ? MF_CHECKED : 0, Hide_, "Hide");
        AppendMenuW(sub, Settings.WhenClaudeClosed == ClosedMode.UsageOnly ? MF_CHECKED : 0, UsageOnly, "Show usage only");
        AppendMenuW(sub, Settings.WhenClaudeClosed == ClosedMode.Show ? MF_CHECKED : 0, Show_, "Keep showing");
        AppendMenuW(m, 0, Open, "Open Claude");
        AppendMenuW(m, 0, Refresh_, "Refresh");
        AppendMenuW(m, MF_SEPARATOR, 0, null);
        AppendMenuW(m, Settings.Notifications ? MF_CHECKED : 0, Notif, "Notifications");
        AppendMenuW(m, Settings.ShowUsage ? MF_CHECKED : 0, Gauge, "Show usage");
        AppendMenuW(m, MF_POPUP, (nuint)sub, "When Claude is closed");
        AppendMenuW(m, MF_POPUP, (nuint)pos, "Position");
        AppendMenuW(m, Shell.Autostart ? MF_CHECKED : 0, Auto, "Start with Windows");
        AppendMenuW(m, 0, Edit, "Open settings file");
        AppendMenuW(m, MF_SEPARATOR, 0, null);
        AppendMenuW(m, 0, Exit, "Exit");
        GetCursorPos(out var pt);
        SetForegroundWindow(Hwnd);
        int cmd = TrackPopupMenu(m, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, 0, Hwnd, 0);
        PostMessageW(Hwnd, WM_NULL, 0, 0);
        DestroyMenu(m); // also destroys the submenu
        switch (cmd)
        {
            case Open: Shell.OpenClaude(); break;
            case Refresh_: Program.Reload(force: true); break;
            case Notif: Settings.Notifications = !Settings.Notifications; Settings.Save(); break;
            case Gauge: Settings.ShowUsage = !Settings.ShowUsage; Settings.Save(); Refresh(); break;
            case Hide_: Settings.WhenClaudeClosed = ClosedMode.Hide; Settings.Save(); Refresh(); break;
            case UsageOnly: Settings.WhenClaudeClosed = ClosedMode.UsageOnly; Settings.Save(); Refresh(); break;
            case Show_: Settings.WhenClaudeClosed = ClosedMode.Show; Settings.Save(); Refresh(); break;
            case PosLeft: Settings.Left = true; Settings.Save(); Refresh(force: true); break;
            case PosRight: Settings.Left = false; Settings.Save(); Refresh(force: true); break;
            case Auto: Shell.Autostart = !Shell.Autostart; break;
            case Edit: Shell.OpenFile(Paths.SettingsFile); break;
            case Exit: DestroyWindow(Hwnd); break;
        }
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint h, uint msg, nint w, nint l)
    {
        try
        {
            switch (msg)
            {
                case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
                case WM_MOUSEMOVE:
                {
                    if (!tracking)
                    {
                        var t = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE, hwndTrack = h };
                        TrackMouseEvent(&t); tracking = true;
                    }
                    int r = RegionAt((short)(l & 0xFFFF));
                    if (r != hover)
                    {
                        hover = r;
                        if (Tooltip.Visible) ShowHoverTip(); // already showing: switch at once
                        else if (r >= 0) SetTimer(h, Program.T_HOVER, 300, 0);
                    }
                    return 0;
                }
                case WM_MOUSELEAVE:
                    tracking = false; hover = -1; KillTimer(h, Program.T_HOVER); Tooltip.Hide();
                    return 0;
                case WM_LBUTTONUP: Tooltip.Hide(); Shell.OpenClaude(); return 0;
                case WM_RBUTTONUP: Tooltip.Hide(); Menu(); return 0;
                case WM_TIMER: Program.OnTimer((nuint)w); return 0;
                case Notify.CallbackMsg: Notify.OnCallback(l); return 0;
                case WM_DISPLAYCHANGE:
                case WM_DPICHANGED:
                case WM_SETTINGCHANGE:
                    Refresh(force: true); return 0;
                case WM_DESTROY:
                    Notify.Remove();
                    if (hookLoc != 0) UnhookWinEvent(hookLoc);
                    if (hookFg != 0) UnhookWinEvent(hookFg);
                    PostQuitMessage(0);
                    return 0;
            }
            if (msg == TaskbarCreatedMsg && msg != 0) // Explorer restarted
            {
                taskbar = 0; AttachTaskbar(); Refresh(force: true);
                return 0;
            }
        }
        catch { }
        return DefWindowProcW(h, msg, w, l);
    }
}
