// Shell actions: open/focus Claude, autostart, open the settings file.
using static ClaudeTaskbar.Native;

namespace ClaudeTaskbar;

static unsafe class Shell
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "ClaudeTaskbar";
    static string? wantAumid;
    static nint found;

    /// <summary>Focuses the window carrying the configured AppUserModelID (the Claude launcher's Terminal); otherwise launches Claude.</summary>
    public static void OpenClaude()
    {
        if (Settings.FocusAumid.Length > 0)
        {
            wantAumid = Settings.FocusAumid; found = 0;
            EnumWindows(&EnumProc, 0);
            if (found != 0)
            {
                if (IsIconic(found) != 0) ShowWindow(found, SW_RESTORE);
                SetForegroundWindow(found);
                return;
            }
        }
        var (file, args) = LaunchTarget();
        ShellExecuteW(0, "open", file, args, Paths.Home, 1);
    }

    static (string file, string? args) LaunchTarget()
    {
        var cmd = Settings.LaunchCommand.Trim();
        if (cmd.Length > 0)
        {
            if (cmd[0] == '"' && cmd.IndexOf('"', 1) is var q and > 0) return (cmd[1..q], cmd[(q + 1)..].Trim() is { Length: > 0 } a ? a : null);
            var sp = cmd.IndexOf(' ');
            return sp < 0 ? (cmd, null) : (cmd[..sp], cmd[(sp + 1)..]);
        }
        if (File.Exists(Paths.LauncherExe)) return (Paths.LauncherExe, null);
        return ("wt.exe", "claude agents");
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static int EnumProc(nint h, nint l)
    {
        try
        {
            if (IsWindowVisible(h) == 0) return 1;
            if (Aumid(h) == wantAumid) { found = h; return 0; }
        }
        catch { }
        return 1;
    }

    static string? Aumid(nint hwnd)
    {
        var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"); // IPropertyStore
        if (SHGetPropertyStoreForWindow(hwnd, &iid, out var store) != 0 || store == 0) return null;
        var vtbl = *(nint**)store;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 }; // System.AppUserModel.ID
            byte* pv = stackalloc byte[24];
            new Span<byte>(pv, 24).Clear();
            var getValue = (delegate* unmanaged<nint, PROPERTYKEY*, byte*, int>)vtbl[5];
            if (getValue(store, &key, pv) != 0) return null;
            string? r = *(ushort*)pv == 31 /* VT_LPWSTR */ && *(char**)(pv + 8) != null ? new string(*(char**)(pv + 8)) : null;
            PropVariantClear(pv);
            return r;
        }
        finally { ((delegate* unmanaged<nint, uint>)vtbl[2])(store); }
    }

    public static bool Autostart
    {
        get => RegString(RunKey, RunValue) != null;
        set
        {
            if (value)
            {
                var v = $"\"{Environment.ProcessPath}\"";
                RegSetKeyValueW(HKEY_CURRENT_USER, RunKey, RunValue, REG_SZ, v, (uint)((v.Length + 1) * 2));
            }
            else RegDeleteKeyValueW(HKEY_CURRENT_USER, RunKey, RunValue);
        }
    }

    public static void OpenFile(string path)
    {
        if (ShellExecuteW(0, "open", path, null, null, 1) <= 32) ShellExecuteW(0, "open", "notepad.exe", $"\"{path}\"", null, 1);
    }
}
