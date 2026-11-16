// Hover popup above the strip: per-status session list, or usage + reset countdowns. Click-through, never activates.
using System.Runtime.InteropServices;
using static ClaudeTaskbar.Native;

namespace ClaudeTaskbar;

static unsafe class Tooltip
{
    enum Kind { Header, Row, Note }
    record struct Line(Kind Kind, string[] Cells, uint Dot = 0);

    static nint hwnd;
    static List<Line> lines = [];
    static Status? showing;
    static int anchorX, anchorTop;
    static uint dpi = 96;
    static int[] colW = [];
    static int lineH, pad, gap, dotD;
    public static bool Visible { get; private set; }

    static void EnsureWindow()
    {
        if (hwnd != 0) return;
        var inst = GetModuleHandleW(0);
        var cls = (char*)Marshal.StringToHGlobalUni("ClaudeTaskbarTip");
        var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = inst, lpszClassName = cls, hCursor = LoadCursorW(0, 32512) };
        RegisterClassExW(&wc);
        hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT, "ClaudeTaskbarTip", null, WS_POPUP, 0, 0, 1, 1, Strip.Hwnd, 0, inst, 0);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, &round, 4);
    }

    static bool Light => RegDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) == 1;

    public static void Show(Status? status, int centerX, int stripTop, uint d)
    {
        EnsureWindow();
        showing = status; anchorX = centerX; anchorTop = stripTop; dpi = d;
        Build();
        Layout();
        if (status == null) SetTimer(Strip.Hwnd, Program.T_TIP, 30_000, 0); else KillTimer(Strip.Hwnd, Program.T_TIP);
    }

    public static void RefreshIfOpen()
    {
        if (!Visible) return;
        Build();
        if (lines.Count == 0) { Hide(); return; }
        Layout();
    }

    public static void Hide()
    {
        if (!Visible) return;
        ShowWindow(hwnd, SW_HIDE);
        Visible = false;
        KillTimer(Strip.Hwnd, Program.T_TIP);
        Program.ScheduleTrim();
    }

    static void Build()
    {
        lines = [];
        if (showing is { } st)
        {
            var list = Sessions.Items.Where(s => s.Status == st).ToList();
            lines.Add(new Line(Kind.Header, [$"{StatusInfo.Label(st)} ({list.Count})"], StatusInfo.Color(st, Light)));
            foreach (var s in list.Take(8)) lines.Add(new Line(Kind.Row, [s.Name, Sessions.LastMessage(s)]));
            if (list.Count > 8) lines.Add(new Line(Kind.Note, [$"+{list.Count - 8} more"]));
            return;
        }
        if (!Usage.HasData)
        {
            lines.Add(new Line(Kind.Note, ["No usage data yet: it updates while Claude Code runs (taskbar-usage plugin)."]));
            return;
        }
        lines.Add(new Line(Kind.Header, ["Usage limits"]));
        AddWindow("5-hour", Usage.FiveHour);
        AddWindow("Weekly", Usage.Weekly);
        if (Usage.WrittenAt is { } t) lines.Add(new Line(Kind.Note, [$"updated {Usage.Ago(t)}"]));
    }

    static void AddWindow(string label, Usage.Window w)
    {
        if (w.Pct == null) return;
        if (Usage.Current(w) is { } p) lines.Add(new Line(Kind.Row, [label, $"{p:0}%", w.Reset is { } r ? Usage.Countdown(r) : ""]));
        else lines.Add(new Line(Kind.Row, [label, "–", "reset · waiting for fresh data"]));
    }

    static (nint normal, nint bold) Fonts()
    {
        float s = dpi / 96f;
        int h = -(int)MathF.Round(12 * s);
        return (CreateFontW(h, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI"), CreateFontW(h, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI"));
    }

    static void Layout()
    {
        float s = dpi / 96f;
        pad = (int)(10 * s); gap = (int)(14 * s); dotD = (int)(8 * s);
        var (fn, fb) = Fonts();
        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        ReleaseDC(0, screen);
        var old = SelectObject(dc, fn);

        int cols = lines.Where(l => l.Kind == Kind.Row).Select(l => l.Cells.Length).DefaultIfEmpty(0).Max();
        colW = new int[cols];
        int spanW = 0, textH = 0;
        foreach (var l in lines)
        {
            SelectObject(dc, l.Kind == Kind.Header ? fb : fn);
            if (l.Kind == Kind.Row)
                for (int c = 0; c < l.Cells.Length; c++)
                {
                    GetTextExtentPoint32W(dc, l.Cells[c], l.Cells[c].Length, out var sz);
                    textH = Math.Max(textH, sz.Cy);
                    int cap = c == 0 && cols > 2 ? int.MaxValue : c == 0 ? (int)(190 * s) : (int)(340 * s);
                    colW[c] = Math.Max(colW[c], Math.Min(sz.Cx, cap));
                }
            else
            {
                GetTextExtentPoint32W(dc, l.Cells[0], l.Cells[0].Length, out var sz);
                textH = Math.Max(textH, sz.Cy);
                spanW = Math.Max(spanW, Math.Min(sz.Cx, (int)(520 * s)) + (l.Dot != 0 ? dotD + (int)(6 * s) : 0));
            }
        }
        SelectObject(dc, old); DeleteDC(dc); DeleteObject(fn); DeleteObject(fb);

        lineH = textH + (int)(5 * s);
        int rowsW = colW.Sum() + Math.Max(0, cols - 1) * gap;
        int w = pad * 2 + Math.Max(spanW, rowsW);
        int h = pad * 2 + lines.Count * lineH - (int)(3 * s);

        var mon = MonitorFromWindow(Strip.Hwnd, 2);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfoW(mon, &mi);
        int x = Math.Clamp(anchorX - w / 2, mi.rcMonitor.Left + (int)(4 * s), mi.rcMonitor.Right - w - (int)(4 * s));
        int y = anchorTop - h - (int)(8 * s);
        if (y < mi.rcMonitor.Top) y = anchorTop + (int)(56 * s); // taskbar at the top (not standard on Windows 11)

        uint border = Light ? Rgb(0xD0, 0xD0, 0xD0) : Rgb(0x48, 0x48, 0x48);
        DwmSetWindowAttribute(hwnd, 34, &border, 4);
        SetWindowPos(hwnd, -1, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(hwnd, null, 0);
        Visible = true;
    }

    static void Paint(nint dc)
    {
        GetClientRect(hwnd, out var rc);
        bool light = Light;
        uint bg = light ? Rgb(0xF9, 0xF9, 0xF9) : Rgb(0x2B, 0x2B, 0x2B);
        uint fg = light ? Rgb(0x1A, 0x1A, 0x1A) : Rgb(0xF2, 0xF2, 0xF2);
        uint dim = light ? Rgb(0x5C, 0x5C, 0x5C) : Rgb(0xA8, 0xA8, 0xA8);

        var mem = CreateCompatibleDC(dc);
        var bmi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = rc.Width, biHeight = -rc.Height, biPlanes = 1, biBitCount = 32 };
        var bmp = CreateDIBSection(dc, &bmi, 0, out var bitsPtr, 0, 0);
        var px = (uint*)bitsPtr;
        var oldBmp = SelectObject(mem, bmp);
        var brush = CreateSolidBrush(bg);
        FillRect(mem, &rc, brush);
        DeleteObject(brush);

        var (fn, fb) = Fonts();
        var oldFont = SelectObject(mem, fn);
        SetBkMode(mem, TRANSPARENT);
        const uint fmt = DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX | DT_VCENTER;
        float s = dpi / 96f;
        int y = pad;
        foreach (var l in lines)
        {
            var r = new RECT { Left = pad, Top = y, Right = rc.Right - pad, Bottom = y + lineH };
            if (l.Kind == Kind.Row)
            {
                int x = pad;
                for (int c = 0; c < l.Cells.Length && c < colW.Length; c++)
                {
                    var cr = new RECT { Left = x, Top = y, Right = c == l.Cells.Length - 1 ? rc.Right - pad : x + colW[c], Bottom = y + lineH };
                    SelectObject(mem, fn);
                    SetTextColor(mem, c == l.Cells.Length - 1 && c > 0 ? dim : fg);
                    DrawTextW(mem, l.Cells[c], -1, &cr, fmt);
                    x += colW[c] + gap;
                }
            }
            else
            {
                if (l.Dot != 0)
                {
                    GdiFlush();
                    Dot(px, rc.Width, rc.Height, pad + dotD / 2, y + lineH / 2, dotD / 2f, l.Dot, bg);
                    r.Left += dotD + (int)(6 * s);
                }
                SelectObject(mem, l.Kind == Kind.Header ? fb : fn);
                SetTextColor(mem, l.Kind == Kind.Note ? dim : fg);
                DrawTextW(mem, l.Cells[0], -1, &r, fmt);
            }
            y += lineH;
        }
        Native.BitBlt(dc, 0, 0, rc.Width, rc.Height, mem, 0, 0, 0x00CC0020);
        SelectObject(mem, oldFont); SelectObject(mem, oldBmp);
        DeleteObject(fn); DeleteObject(fb); DeleteObject(bmp); DeleteDC(mem);
    }

    /// <summary>Anti-aliased dot (0xRRGGBB) blended onto the opaque background (COLORREF).</summary>
    static void Dot(uint* px, int W, int H, int cx, int cy, float r, uint rgb, uint bgRef)
    {
        int br = (int)(bgRef & 0xFF), bgG = (int)((bgRef >> 8) & 0xFF), bb = (int)((bgRef >> 16) & 0xFF);
        int fr = (int)((rgb >> 16) & 0xFF), fg = (int)((rgb >> 8) & 0xFF), fb = (int)(rgb & 0xFF);
        for (int y = (int)(cy - r - 1); y <= (int)(cy + r + 1); y++)
            for (int x = (int)(cx - r - 1); x <= (int)(cx + r + 1); x++)
            {
                if ((uint)x >= (uint)W || (uint)y >= (uint)H) continue;
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                float a = Math.Clamp(r + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
                if (a <= 0) continue;
                int R = (int)(br + (fr - br) * a), G = (int)(bgG + (fg - bgG) * a), B = (int)(bb + (fb - bb) * a);
                px[y * W + x] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | (uint)B;
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
                case WM_NCHITTEST: return -1; // HTTRANSPARENT
                case WM_ERASEBKGND: return 1;
                case WM_PAINT:
                {
                    byte* ps = stackalloc byte[80];
                    var dc = BeginPaint(h, ps);
                    Paint(dc);
                    EndPaint(h, ps);
                    return 0;
                }
            }
        }
        catch { }
        return DefWindowProcW(h, msg, w, l);
    }
}
