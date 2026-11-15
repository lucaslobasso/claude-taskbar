// Status-change toasts through Shell_NotifyIcon balloons (Windows 11 shows them as normal toasts).
// A tray icon exists only while a toast is up, then it is removed. No WinRT, no extra size.
using static ClaudeTaskbar.Native;

namespace ClaudeTaskbar;

static unsafe class Notify
{
    public const uint CallbackMsg = WM_APP + 1;
    static bool added;
    static nint smallIcon, largeIcon;

    public static void Show(List<Change> changes)
    {
        if (!Settings.Notifications) return;
        var hits = changes.Where(c => Settings.NotifyOn.Contains(c.To)).ToList();
        if (hits.Count == 0) return;

        string title, body;
        if (hits.Count == 1)
        {
            var c = hits[0];
            title = c.Name;
            body = c.Detail.Length > 0 ? $"{StatusInfo.Label(c.To)} · {Json.Short(c.Detail, 180)}" : StatusInfo.Label(c.To);
        }
        else
        {
            title = $"{hits.Count} Claude sessions changed";
            body = string.Join("\n", hits.Take(4).Select(c => $"{StatusInfo.Label(c.To)}: {c.Name}")) + (hits.Count > 4 ? "\n…" : "");
        }

        EnsureIcons();
        var nid = Base();
        if (!added)
        {
            nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            Copy("Claude Taskbar", nid.szTip, 128);
            if (Shell_NotifyIconW(NIM_ADD, &nid) == 0) { Log.Write("notify: could not add the tray icon"); return; }
            nid.uVersion = 4;
            Shell_NotifyIconW(NIM_SETVERSION, &nid);
            added = true;
        }
        nid = Base();
        nid.uFlags = NIF_INFO;
        Copy(title, nid.szInfoTitle, 64);
        Copy(body, nid.szInfo, 256);
        nid.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
        nid.hBalloonIcon = largeIcon;
        if (Shell_NotifyIconW(NIM_MODIFY, &nid) == 0) Log.Write("notify: could not show the toast");
        else if (Log.Debug) Log.Write($"notify: {title} | {body.Replace('\n', ' ')}");
        SetTimer(Strip.Hwnd, Program.T_NOTIFY, 30_000, 0); // safety: remove the icon even if no callback arrives
    }

    public static void OnCallback(nint lParam)
    {
        uint ev = (uint)(lParam & 0xFFFF);
        if (ev == NIN_BALLOONUSERCLICK) { Remove(); Shell.OpenClaude(); }
        else if (ev is NIN_BALLOONTIMEOUT or NIN_BALLOONHIDE) Remove();
    }

    public static void Remove()
    {
        KillTimer(Strip.Hwnd, Program.T_NOTIFY);
        if (!added) return;
        var nid = Base();
        Shell_NotifyIconW(NIM_DELETE, &nid);
        added = false;
    }

    static NOTIFYICONDATAW Base() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = Strip.Hwnd, uID = 1, uCallbackMessage = CallbackMsg, hIcon = smallIcon,
    };

    static void Copy(string s, char* dst, int max)
    {
        int n = Math.Min(s.Length, max - 1);
        for (int i = 0; i < n; i++) dst[i] = s[i];
        dst[n] = '\0';
    }

    static void EnsureIcons()
    {
        if (smallIcon != 0) return;
        if (File.Exists(Paths.ClaudeExe) && ExtractIconExW(Paths.ClaudeExe, 0, out var l, out var sm, 1) > 0 && l != 0)
        {
            largeIcon = l; smallIcon = sm != 0 ? sm : l;
            return;
        }
        largeIcon = Disc(32); smallIcon = Disc(16);
    }

    /// <summary>Fallback icon: a Claude-orange disc.</summary>
    static nint Disc(int size)
    {
        var bmi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        var color = CreateDIBSection(0, &bmi, 0, out var bits, 0, 0);
        var px = (uint*)bits;
        float r = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - r, dy = y + 0.5f - r;
                int a = (int)(Math.Clamp(r - 0.5f - MathF.Sqrt(dx * dx + dy * dy) + 0.5f, 0, 1) * 255);
                px[y * size + x] = ((uint)a << 24) | ((uint)(0xD9 * a / 255) << 16) | ((uint)(0x77 * a / 255) << 8) | (uint)(0x57 * a / 255);
            }
        var mask = CreateBitmap(size, size, 1, 1, null);
        var ii = new ICONINFO { fIcon = 1, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(&ii);
        DeleteObject(color); DeleteObject(mask);
        return icon;
    }
}
