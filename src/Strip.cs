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

    const int GaugeKind = 100; // 100 = 5-hour bar, 101 = weekly bar
    static bool IsGauge(int kind) => kind >= GaugeKind;
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
            if (!Sessions.ClaudeOpen) SetTimer(Hwnd, Program.T_DEBOUNCE, 600, 0); // a terminal may have just opened: look for Claude clients
        }
    }

    public static bool IsFullscreen() => SHQueryUserNotificationState(out var s) == 0 && s is 2 or 3 or 4; // busy, D3D fullscreen, presentation

    // ---- content -----------------------------------------------------------------------------------------------

    record struct Item(int Kind, string Text, uint Color, double Fill, uint TextColor = 0xFFFFFF); // Kind: (int)Status or GaugeKind

    /// <summary>Items left to right. Logical order starts at the anchored edge (usage, then needs input ... idle);
    /// on the right side the strip is mirrored so usage sits next to the tray.</summary>
    static List<Item> BuildItems(bool light)
    {
        var items = new List<Item>();
        bool open = Sessions.ClaudeOpen;
        bool sessions = open || Settings.WhenClaudeClosed == ClosedMode.Show;
        bool gauge = Settings.ShowUsage && Usage.HasData && (open || Settings.WhenClaudeClosed != ClosedMode.Hide);
        if (gauge)
        {
            // one bar per window, 5-hour first (nearest the edge), then weekly
            void Bar(int kind, Usage.Window w)
            {
                if (w.Pct == null) return;
                double? p = Usage.Current(w);
                uint col = p is >= 90 ? 0xE5534Bu : p is >= 70 ? 0xE8B04Bu : 0u;
                string text = !Settings.ShowUsagePercent ? "" : p is { } v ? $"{Math.Round(v):0}%" : "–";
                items.Add(new Item(kind, text, col, Math.Clamp((p ?? 0) / 100.0, 0, 1)));
            }
            Bar(GaugeKind, Usage.FiveHour);
            Bar(GaugeKind + 1, Usage.Weekly);
        }
        if (sessions)
            foreach (var s in Enum.GetValues<Status>().OrderByDescending(x => x)) // idle ... needs input, outward from the edge
            {
                if (Settings.Hidden.Contains(s)) continue;
                var n = Sessions.Count(s);
                if (n > 0) items.Add(new Item((int)s, n.ToString(), StatusInfo.Color(s, light), 0, StatusInfo.TextColor(s, light)));
            }
        if (!Settings.Left) items.Reverse();
        return items;
    }

    static bool LightTheme() => RegDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) == 1;

    /// <summary>Re-reads position and content; redraws only if something visible changed.</summary>
    public static void Refresh(bool force = false)
    {
        if (taskbar == 0 || IsWindow(taskbar) == 0 || tray == 0 || IsWindow(tray) == 0) AttachTaskbar();
        bool light = LightTheme();
        var items = BuildItems(light);
        if (taskbar == 0 || Fullscreen || items.Count == 0) { Hide(); return; }

        GetWindowRect(taskbar, out var tb);
        RECT tr = default;
        if (tray != 0) GetWindowRect(tray, out tr); else tr.Left = tb.Right;
        dpi = GetDpiForWindow(taskbar);
        if (dpi == 0) dpi = 96;

        var sig = $"{tb.Left},{tb.Top},{tb.Right},{tb.Bottom},{tr.Left},{dpi},{light},{Settings.OffsetX},{Settings.Left}|" +
                  string.Join(";", items.Select(i => $"{i.Kind}:{i.Text}:{i.Color}:{i.TextColor}:{i.Fill:0.000}"));
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
        int pad = R(6 * s), dotD = R(18 * s), dotPad = R(5 * s), gap = R(5 * s), groupGap = R(9 * s), barW = R(48 * s);

        var font = CreateFontW(-R(12 * s), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4 /* ANTIALIASED_QUALITY: grayscale coverage */, 0, "Segoe UI");
        var boldFont = CreateFontW(-R(11 * s), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        ReleaseDC(0, screen);
        var oldFont = SelectObject(dc, font);

        // layout, left to right in the order given (BuildItems already mirrored it for the right side)
        var widths = new int[items.Count]; // item width (dot: pill; ring: ring + gap + label)
        var textW = new int[items.Count];
        int textH = 0, W = pad;
        for (int i = 0; i < items.Count; i++)
        {
            bool ring = IsGauge(items[i].Kind);
            SelectObject(dc, ring ? font : boldFont);
            GetTextExtentPoint32W(dc, items[i].Text, items[i].Text.Length, out var sz);
            textW[i] = sz.Cx; textH = Math.Max(textH, sz.Cy);
            widths[i] = ring ? barW + (items[i].Text.Length > 0 ? gap + sz.Cx : 0) : Math.Max(dotD, sz.Cx + 2 * dotPad);
            W += widths[i] + (i < items.Count - 1 ? groupGap : 0);
        }
        W += pad;

        var bmi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32 };
        var bmp = CreateDIBSection(dc, &bmi, 0, out var bitsPtr, 0, 0);
        var oldBmp = SelectObject(dc, bmp);
        var px = (uint*)bitsPtr;

        // 1. all text as white-on-black coverage; remember where each label sits and in which color
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, 0xFFFFFF);
        var regs = new Region[items.Count];
        var spans = new (int x0, int x1, uint color)[items.Count];
        var shapes = new List<(int x0, int w, Item item)>();
        int x = pad, ty = (H - textH) / 2, cy = H / 2;
        uint fg = light ? 0x1A1A1Au : 0xFFFFFFu;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            int x0 = x, tx;
            if (IsGauge(it.Kind)) { tx = x + barW + gap; SelectObject(dc, font); spans[i] = (tx, tx + textW[i], fg); }
            else { tx = x + (widths[i] - textW[i]) / 2; SelectObject(dc, boldFont); spans[i] = (tx, tx + textW[i], it.TextColor); }
            TextOutW(dc, tx, ty, it.Text, it.Text.Length);
            shapes.Add((x0, widths[i], it));
            x += widths[i];
            int g = i < items.Count - 1 ? groupGap : 0;
            regs[i] = new Region(x0 - (i == 0 ? pad : groupGap / 2), x + (i == items.Count - 1 ? pad : g / 2), it.Kind);
            x += g;
        }
        GdiFlush();
        var cov = new byte[W * H];
        for (int i = 0; i < W * H; i++)
        {
            uint c = px[i];
            cov[i] = (byte)Math.Max((int)(c & 0xFF), Math.Max((int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF)));
            px[i] = 0x01000000u; // alpha 1 keeps the whole strip clickable
        }

        // 2. shapes: colored dots/pills, usage ring
        foreach (var (x0, w, item) in shapes)
        {
            if (IsGauge(item.Kind)) Bar(px, W, H, x0, barW, cy, dotD / 2f, item.Fill, item.Color == 0 ? fg : item.Color, fg);
            else Pill(px, W, H, x0, x0 + w, cy, dotD / 2f, item.Color);
        }

        // 3. text over the shapes, each label in its own color. Segoe UI leaves more room above the digits than
        // below, so every label is shifted until its ink is centered on the dots' center line.
        foreach (var (a, b, color) in spans)
        {
            if (b <= a) continue;
            int lo = H, hi = -1;
            for (int yy = 0; yy < H; yy++)
                for (int xx = Math.Max(0, a - 1); xx < Math.Min(W, b + 1); xx++)
                    if (cov[yy * W + xx] != 0) { lo = Math.Min(lo, yy); hi = Math.Max(hi, yy); }
            if (hi < 0) continue;
            int dy = (int)MathF.Round(cy - (lo + hi + 1) / 2f);
            for (int yy = 0; yy < H; yy++)
            {
                int sy = yy - dy;
                if ((uint)sy >= (uint)H) continue;
                for (int xx = Math.Max(0, a - 1); xx < Math.Min(W, b + 1); xx++)
                {
                    int c = cov[sy * W + xx];
                    if (c == 0) continue;
                    uint d = px[yy * W + xx], t = Premul(color, c);
                    uint inv = (uint)(255 - c);
                    uint oa = Math.Min(255u, (t >> 24) + (((d >> 24) * inv + 127) / 255));
                    uint orr = ((t >> 16) & 0xFF) + ((((d >> 16) & 0xFF) * inv + 127) / 255);
                    uint og = ((t >> 8) & 0xFF) + ((((d >> 8) & 0xFF) * inv + 127) / 255);
                    uint ob = (t & 0xFF) + (((d & 0xFF) * inv + 127) / 255);
                    px[yy * W + xx] = (oa << 24) | (Math.Min(255u, orr) << 16) | (Math.Min(255u, og) << 8) | Math.Min(255u, ob);
                }
            }
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
        DeleteObject(bmp); DeleteObject(font); DeleteObject(boldFont); DeleteDC(dc);
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

    /// <summary>Capsule from x0 to x1 (a disc when x1 - x0 == 2r), centered on cy. Optional alpha and a right clip edge.</summary>
    static void Pill(uint* px, int W, int H, int x0, int x1, int cy, float r, uint rgb, float alpha = 1f, float limit = float.MaxValue)
    {
        float a = x0 + r, b = x1 - r;
        for (int y = (int)(cy - r - 1); y <= (int)(cy + r + 1); y++)
            for (int x = x0 - 1; x <= x1 + 1; x++)
            {
                float fx = Math.Clamp(x + 0.5f, a, b), dx = x + 0.5f - fx, dy = y + 0.5f - cy;
                float cov = Math.Clamp(r + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f) * alpha * Math.Clamp(limit - x, 0f, 1f);
                Put(px, W, H, x, y, rgb, cov);
            }
    }

    /// <summary>Usage bar as tall as the status dots: a faint track with the used part filled.</summary>
    static void Bar(uint* px, int W, int H, int x0, int w, int cy, float r, double fill, uint rgb, uint track)
    {
        Pill(px, W, H, x0, x0 + w, cy, r, track, 0.28f);
        if (fill > 0) Pill(px, W, H, x0, x0 + w, cy, r, rgb, 1f, x0 + (float)(fill * w));
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
        Tooltip.Show(IsGauge(r.Kind) ? null : (Status)r.Kind, stripX + (r.X0 + r.X1) / 2, stripY, dpi);
    }

    static void Menu()
    {
        const int Open = 1, Refresh_ = 2, Notif = 10, Hide_ = 20, UsageOnly = 21, Show_ = 22, Gauge = 30, GaugePct = 31, Auto = 40, Edit = 50,
            PosLeft = 60, PosRight = 61, Exit = 99;
        var pos = CreatePopupMenu();
        AppendMenuW(pos, Settings.Left ? MF_CHECKED : 0, PosLeft, "Left side (usage on the left)");
        AppendMenuW(pos, Settings.Left ? 0 : MF_CHECKED, PosRight, "Right side (usage next to the tray)");
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
        AppendMenuW(m, (Settings.ShowUsagePercent ? MF_CHECKED : 0) | (Settings.ShowUsage ? 0 : MF_GRAYED), GaugePct, "Show usage percent");
        AppendMenuW(m, MF_POPUP, (nuint)sub, "When no terminal is open and nothing runs");
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
            case GaugePct: Settings.ShowUsagePercent = !Settings.ShowUsagePercent; Settings.Save(); Refresh(); break;
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
