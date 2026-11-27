using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

// RokeBox: more control of the computer from the phone. Four widgets, each acting only for the widget the phone names
// ("pageId/widgetId") on an enabled page it shows, and only with that widget's own switch on (off by default, set on the
// computer, like Dictation and the Steam library's "start games"):
//   windows       the open windows: bring one to the front, minimize, maximize or close it (WM_CLOSE, as its X button)
//   keyboard      type text and press keys from a fixed list into the app in front (no free key combinations)
//   screen        a picture of one monitor, and a click where the phone was tapped (two switches: see, then click)
//   quickactions  display mode, light/dark theme and a fixed set of Windows / Xbox Game Bar shortcuts
// Parameters come in the query string: the phone's WebView sends POSTs without a body (see /api/dictate). Nothing the
// phone sends is logged, nor are window titles or screen pictures.

public sealed record RkWindow(long Id, string Title, string App, string Process, bool Minimized, bool Maximized, bool Active);
public sealed record RkWindowsView(bool Supported, bool Control, IReadOnlyList<RkWindow> Windows);

/// <summary>The Windows widget: the computer's open windows, in the order they are stacked (the one in front first).</summary>
public sealed class WindowControl(ConfigStore config, ILogger<WindowControl> log)
{
    public const string WidgetType = "windows";
    public const int MaxWindows = 40;
    public static readonly string[] Actions = ["focus", "minimize", "maximize", "close"];
    public const string Off = "Turn on \"Let the phone switch, minimize and close windows\" in this widget's settings";

    internal Func<IReadOnlyList<RkWindow>> List { get; set; } = RkNative.Windows;
    internal Func<long, string, bool> Apply { get; set; } = RkNative.ApplyToWindow;

    public (int Status, object Body) View(string? deviceId, string? widget)
    {
        if (PanelWidgets.Find(config.Get(), deviceId, widget, WidgetType) is not { } w)
            return (404, new { error = "this windows widget isn't on an enabled page of this phone" });
        var control = w.Bool("control") == true;
        if (!OperatingSystem.IsWindows()) return (200, new RkWindowsView(false, control, []));
        try { return (200, new RkWindowsView(true, control, List())); }
        catch (Exception ex)
        {
            log.LogWarning("windows: listing failed ({Type})", ex.GetType().Name);
            return (500, new { error = "the windows could not be listed" });
        }
    }

    public (int Status, string? Error) Act(string? deviceId, string? widget, long? id, string? action)
    {
        if (action is null || Array.IndexOf(Actions, action) < 0) return (400, "unknown action");
        if (PanelWidgets.Find(config.Get(), deviceId, widget, WidgetType) is not { } w)
            return (404, "this windows widget isn't on an enabled page of this phone");
        if (w.Bool("control") != true) return (403, Off);
        if (!OperatingSystem.IsWindows()) return (501, "only on Windows");
        // only a window the list would show: the phone cannot name a hidden or system window by its handle
        if (id is not { } handle || !List().Any(x => x.Id == handle)) return (404, "that window is gone");
        return Apply(handle, action) ? (200, null) : (409, "Windows did not let the agent do that (the window may run as administrator)");
    }
}

/// <summary>The Keyboard widget: text and a fixed set of keys, typed into the app in front.</summary>
public sealed class RemoteKeys(ConfigStore config, ILogger<RemoteKeys> log)
{
    public const string WidgetType = "keyboard";
    public const string Off = "Turn on \"Type and press keys on the computer\" in this widget's settings";

    /// <summary>The keys the phone may press, by name. Anything else is refused: the phone never sends a key combination.</summary>
    public static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>
    {
        ["esc"] = "esc", ["tab"] = "tab", ["enter"] = "enter", ["backspace"] = "backspace", ["delete"] = "delete", ["space"] = "space",
        ["up"] = "up", ["down"] = "down", ["left"] = "left", ["right"] = "right",
        ["home"] = "home", ["end"] = "end", ["pageup"] = "pageup", ["pagedown"] = "pagedown",
        ["copy"] = "ctrl+c", ["paste"] = "ctrl+v", ["cut"] = "ctrl+x", ["undo"] = "ctrl+z", ["redo"] = "ctrl+y",
        ["selectall"] = "ctrl+a", ["save"] = "ctrl+s", ["find"] = "ctrl+f",
        ["alttab"] = "alt+tab", ["win"] = "win", ["closewindow"] = "alt+f4", ["menu"] = "shift+f10",
        ["newtab"] = "ctrl+t", ["closetab"] = "ctrl+w", ["reload"] = "f5", ["fullscreen"] = "f11",
        ["playpause"] = "playpause", ["mute"] = "mute", ["volumeup"] = "volumeup", ["volumedown"] = "volumedown",
    };

    internal Func<string, CancellationToken, bool> TypeText { get; set; } = KeySender.TryTypeText;
    internal Func<List<string>, bool> SendCombo { get; set; } = KeySender.SendCombo;
    internal Func<FocusedApp?> Focused { get; set; } = AppConsole.FocusedNow;
    internal bool CheckElevation { get; set; } = OperatingSystem.IsWindows();
    internal bool AgentElevated { get; set; } = Environment.IsPrivilegedProcess;

    private readonly SemaphoreSlim _sending = new(1, 1);

    public async Task<(int Status, string? Error)> SendAsync(string? deviceId, string? widget, string? text, string? key, CancellationToken ct)
    {
        if (PanelWidgets.Find(config.Get(), deviceId, widget, WidgetType) is not { } w)
            return (404, "this keyboard widget isn't on an enabled page of this phone");
        if (w.Bool("control") != true) return (403, Off);
        string? spec = null;
        if (key is not null && !Keys.TryGetValue(key, out spec)) return (400, "unknown key");
        if (spec is null && (text ?? "").Length > Dictation.MaxChars * 2) return (413, "the text is too long");
        var line = spec is null ? Dictation.Clean(text) : "";
        if (spec is null && line.Length == 0) return (400, "nothing to type");
        if (CheckElevation && Focused() is { Elevated: true } && !AgentElevated)
            return (409, "the app in front runs as administrator, so Windows doesn't let the agent type into it");

        if (!await _sending.WaitAsync(TimeSpan.FromSeconds(2), ct)) return (429, "still typing the previous text");
        try
        {
            ct.ThrowIfCancellationRequested();
            if (spec is not null)
            {
                foreach (var combo in KeySender.Parse(spec))
                    if (!SendCombo(combo)) return (500, "the computer refused the key");
            }
            else if (!TypeText(line, ct)) return (500, "the computer refused the typing");
            return (200, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("keyboard: sending failed ({Type})", ex.GetType().Name); // never the text
            return (500, "the keys could not be sent");
        }
        finally { _sending.Release(); }
    }
}

public sealed record RkMonitor(int Index, int Width, int Height, bool Primary);

/// <summary>The Screen widget: a JPEG of one monitor (with a ring where the pointer is), and a click at a tapped point.</summary>
public sealed class ScreenView(ConfigStore config, ILogger<ScreenView> log)
{
    public const string WidgetType = "screen";
    public const string OffView = "Turn on \"Let the phone see this computer's screen\" in this widget's settings";
    public const string OffClick = "Turn on \"Click where the picture is tapped\" in this widget's settings";
    public const int MinWidth = 320, MaxWidth = 1600, DefaultWidth = 1024;
    /// <summary>The shortest time between two pictures, whoever asks: a stuck loop on the phone cannot keep the CPU busy.</summary>
    public const int MinIntervalMs = 400;
    public static readonly string[] Buttons = ["left", "right", "double"];

    internal Func<IReadOnlyList<RkMonitor>> Monitors { get; set; } = RkNative.Monitors;
    internal Func<int, int, byte[]?> Grab { get; set; } = RkNative.CaptureJpeg;
    internal Func<int, double, double, string, bool> ClickAt { get; set; } = RkNative.ClickAt;
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    private readonly Lock _gate = new();
    private long _lastAt = long.MinValue / 2;

    public (int Status, object Body) Info(string? deviceId, string? widget)
    {
        if (PanelWidgets.Find(config.Get(), deviceId, widget, WidgetType) is not { } w)
            return (404, new { error = "this screen widget isn't on an enabled page of this phone" });
        var view = w.Bool("view") == true;
        var supported = OperatingSystem.IsWindows();
        return (200, new { supported, view, click = view && w.Bool("click") == true, monitors = supported && view ? Monitors() : [] });
    }

    public (int Status, byte[]? Jpeg, string? Error) Capture(string? deviceId, string? widget, int? monitor, int? width)
    {
        if (PanelWidgets.Find(config.Get(), deviceId, widget, WidgetType) is not { } w)
            return (404, null, "this screen widget isn't on an enabled page of this phone");
        if (w.Bool("view") != true) return (403, null, OffView);
        if (!OperatingSystem.IsWindows()) return (501, null, "only on Windows");
        var index = Math.Max(0, monitor ?? 0);
        var px = Math.Clamp(width ?? DefaultWidth, MinWidth, MaxWidth);
        lock (_gate)
        {
            var now = Clock();
            if (now - _lastAt < MinIntervalMs) return (429, null, "too many pictures: wait a moment");
            _lastAt = now;
        }
        try
        {
            return Grab(index, px) is { } jpeg ? (200, jpeg, null) : (404, null, "that monitor is not connected");
        }
        catch (Exception ex)
        {
            // the lock screen and Windows security prompts are on a desktop no program can capture
            log.LogInformation("screen: no picture ({Type})", ex.GetType().Name);
            return (409, null, "the screen can't be captured right now (locked, or a Windows security prompt is open)");
        }
    }

    public (int Status, string? Error) Click(string? deviceId, string? widget, int? monitor, double? x, double? y, string? button)
    {
        if (PanelWidgets.Find(config.Get(), deviceId, widget, WidgetType) is not { } w)
            return (404, "this screen widget isn't on an enabled page of this phone");
        if (w.Bool("view") != true) return (403, OffView);
        if (w.Bool("click") != true) return (403, OffClick);
        if (!OperatingSystem.IsWindows()) return (501, "only on Windows");
        if (x is not { } fx || y is not { } fy || !double.IsFinite(fx) || !double.IsFinite(fy) || fx is < 0 or > 1 || fy is < 0 or > 1)
            return (400, "the point is outside the picture");
        var b = button ?? "left";
        if (Array.IndexOf(Buttons, b) < 0) return (400, "unknown button");
        return ClickAt(Math.Max(0, monitor ?? 0), fx, fy, b) ? (200, null) : (409, "the click did not reach the computer");
    }
}

/// <summary>The Quick actions widget: display mode, light or dark theme, and a fixed set of Windows shortcuts.</summary>
public sealed class QuickActions(ConfigStore config, ILogger<QuickActions> log)
{
    public const string WidgetType = "quickactions";
    public const string Off = "Turn on \"Let the phone run these actions\" in this widget's settings";

    /// <summary>Shortcuts Windows itself handles (the shell or Xbox Game Bar), by action name.</summary>
    public static readonly IReadOnlyDictionary<string, string> Shortcuts = new Dictionary<string, string>
    {
        ["desktop"] = "win+d", ["screenshot"] = "win+printscreen", ["snip"] = "win+shift+s",
        ["clip"] = "win+alt+g", ["record"] = "win+alt+r", ["gamebar"] = "win+g",
        ["explorer"] = "win+e", ["taskmgr"] = "ctrl+shift+esc", ["settings"] = "win+i", ["actioncenter"] = "win+a",
    };

    /// <summary>DisplaySwitch.exe's modes (Win+P): this screen only, duplicate, extend, the second screen only.</summary>
    public static readonly IReadOnlyDictionary<string, string> Projection = new Dictionary<string, string>
    {
        ["project-pc"] = "/internal", ["project-clone"] = "/clone", ["project-extend"] = "/extend", ["project-second"] = "/external",
    };

    internal Func<List<string>, bool> SendCombo { get; set; } = KeySender.SendCombo;
    internal Func<string, bool> Project { get; set; } = RkNative.Project;
    internal Func<bool?> IsDark { get; set; } = RkNative.IsDarkTheme;
    internal Func<bool, bool> SetDark { get; set; } = RkNative.SetDarkTheme;

    public static bool Known(string? action) =>
        action is "theme-dark" or "theme-light" || (action is not null && (Shortcuts.ContainsKey(action) || Projection.ContainsKey(action)));

    public (int Status, object Body) State(string? deviceId, string? widget)
    {
        if (PanelWidgets.Find(config.Get(), deviceId, widget, WidgetType) is not { } w)
            return (404, new { error = "this quick actions widget isn't on an enabled page of this phone" });
        var supported = OperatingSystem.IsWindows();
        return (200, new { supported, control = w.Bool("control") == true, dark = supported ? IsDark() : null });
    }

    public (int Status, string? Error) Run(string? deviceId, string? widget, string? action)
    {
        if (!Known(action)) return (400, "unknown action");
        if (PanelWidgets.Find(config.Get(), deviceId, widget, WidgetType) is not { } w)
            return (404, "this quick actions widget isn't on an enabled page of this phone");
        if (w.Bool("control") != true) return (403, Off);
        if (!OperatingSystem.IsWindows()) return (501, "only on Windows");
        try
        {
            bool ok;
            if (action is "theme-dark" or "theme-light") ok = SetDark(action == "theme-dark");
            else if (Projection.TryGetValue(action!, out var mode)) ok = Project(mode);
            else ok = KeySender.Parse(Shortcuts[action!]).All(SendCombo);
            return ok ? (200, null) : (500, "the computer refused the action");
        }
        catch (Exception ex)
        {
            log.LogWarning("quick actions: {Action} failed ({Type})", action, ex.GetType().Name);
            return (500, "the action could not run");
        }
    }
}

/// <summary>The Windows calls behind the four widgets. Each one is a no-op (or empty) on Linux and macOS.</summary>
internal static class RkNative
{
    public static IReadOnlyList<RkWindow> Windows()
    {
#if WINDOWS
        var handles = new List<IntPtr>();
        EnumWindows((h, _) => { if (IsListed(h)) handles.Add(h); return handles.Count < WindowControl.MaxWindows * 2; }, IntPtr.Zero);
        var front = GetForegroundWindow();
        var own = (uint)Environment.ProcessId;
        var list = new List<RkWindow>();
        foreach (var h in handles)
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid == own) continue;
            var (process, app) = Owner(pid);
            if (process is "" or "ShellExperienceHost" or "StartMenuExperienceHost" or "SearchHost" or "TextInputHost") continue;
            list.Add(new RkWindow(h.ToInt64(), Title(h), app, process, IsIconic(h), IsZoomed(h), h == front));
            if (list.Count >= WindowControl.MaxWindows) break;
        }
        return list;
#else
        return [];
#endif
    }

    public static bool ApplyToWindow(long id, string action)
    {
#if WINDOWS
        var h = new IntPtr(id);
        switch (action)
        {
            case "focus":
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                // Windows lets a process bring a window forward only if it had the last input: a zero mouse move gives it that
                KeySender.NudgeInput();
                return SetForegroundWindow(h);
            case "minimize": return ShowWindow(h, SW_MINIMIZE) || IsIconic(h);
            case "maximize": ShowWindow(h, IsZoomed(h) ? SW_RESTORE : SW_MAXIMIZE); return true;
            case "close": return PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
#endif
        return false;
    }

    public static IReadOnlyList<RkMonitor> Monitors()
    {
#if WINDOWS
        // in physical pixels, like the pictures: a monitor scaled to 125% would otherwise report 1536 × 864 for 1920 × 1080
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try { return MonitorRects().Select((m, i) => new RkMonitor(i, m.R.Right - m.R.Left, m.R.Bottom - m.R.Top, m.Primary)).ToList(); }
        finally { SetThreadDpiAwarenessContext(old); }
#else
        return [];
#endif
    }

    public static byte[]? CaptureJpeg(int monitor, int maxWidth)
    {
#if WINDOWS
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var monitors = MonitorRects();
            if (monitor >= monitors.Count) return null;
            var r = monitors[monitor].R;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            using var shot = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(shot))
            {
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h), System.Drawing.CopyPixelOperation.SourceCopy);
                // the pointer is not in a screen capture: a ring shows where it is, so a tap can be aimed from it
                if (GetCursorPos(out var p) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom)
                {
                    var size = Math.Max(18f, w / 70f);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using var dark = new System.Drawing.Pen(System.Drawing.Color.FromArgb(200, 0, 0, 0), size / 5);
                    using var lit = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 251, 113, 133), size / 8);
                    float cx = p.X - r.Left, cy = p.Y - r.Top;
                    g.DrawEllipse(dark, cx - size / 2, cy - size / 2, size, size);
                    g.DrawEllipse(lit, cx - size / 2, cy - size / 2, size, size);
                }
            }
            var scale = Math.Min(1.0, (double)maxWidth / w);
            int tw = Math.Max(1, (int)Math.Round(w * scale)), th = Math.Max(1, (int)Math.Round(h * scale));
            using var small = new System.Drawing.Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(shot, 0, 0, tw, th);
            }
            var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var ps = new System.Drawing.Imaging.EncoderParameters(1);
            ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 72L);
            using var ms = new MemoryStream();
            small.Save(ms, codec, ps);
            return ms.ToArray();
        }
        finally { SetThreadDpiAwarenessContext(old); }
#else
        return null;
#endif
    }

    public static bool ClickAt(int monitor, double x, double y, string button)
    {
#if WINDOWS
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var monitors = MonitorRects();
            if (monitor >= monitors.Count) return false;
            var r = monitors[monitor].R;
            var px = r.Left + (int)Math.Min(r.Right - r.Left - 1, Math.Round(x * (r.Right - r.Left)));
            var py = r.Top + (int)Math.Min(r.Bottom - r.Top - 1, Math.Round(y * (r.Bottom - r.Top)));
            if (!SetCursorPos(px, py)) return false;
            if (button == "right") KeySender.MouseRightClick();
            else
            {
                KeySender.MouseClick();
                if (button == "double") { Thread.Sleep(60); KeySender.MouseClick(); }
            }
            return true;
        }
        finally { SetThreadDpiAwarenessContext(old); }
#else
        return false;
#endif
    }

    public static bool Project(string mode)
    {
#if WINDOWS
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe");
        if (!File.Exists(exe)) return false;
        using var p = Process.Start(new ProcessStartInfo(exe, mode) { UseShellExecute = false, CreateNoWindow = true });
        return p is not null;
#else
        return false;
#endif
    }

    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool? IsDarkTheme()
    {
#if WINDOWS
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Personalize);
        return key?.GetValue("AppsUseLightTheme") is int light ? light == 0 : null;
#else
        return null;
#endif
    }

    public static bool SetDarkTheme(bool dark)
    {
#if WINDOWS
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Personalize);
        key.SetValue("AppsUseLightTheme", dark ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
        // the same message Settings sends, so open windows and the taskbar switch now
        SendMessageTimeoutW(new IntPtr(0xFFFF), WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 500, out _);
        return true;
#else
        return false;
#endif
    }

#if WINDOWS
    // ---- windows that the taskbar and Alt+Tab would show
    private static bool IsListed(IntPtr h)
    {
        if (!IsWindowVisible(h) || GetWindowTextLengthW(h) == 0) return false;
        var ex = GetWindowLongPtrW(h, GWL_EXSTYLE).ToInt64();
        var appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if (!appWindow && ((ex & WS_EX_TOOLWINDOW) != 0 || GetWindow(h, GW_OWNER) != IntPtr.Zero)) return false;
        // windows of other virtual desktops and suspended Store apps are "cloaked": there, but not shown
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var cls = ClassOf(h);
        return cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    private static string Title(IntPtr h)
    {
        var buf = new char[Math.Min(512, GetWindowTextLengthW(h) + 1)];
        var n = GetWindowTextW(h, buf, buf.Length);
        return new string(buf, 0, Math.Max(0, n));
    }

    private static string ClassOf(IntPtr h)
    {
        var buf = new char[256];
        var n = GetClassNameW(h, buf, buf.Length);
        return new string(buf, 0, Math.Max(0, n));
    }

    private static readonly Dictionary<uint, (string Process, string App, long At)> Owners = [];

    /// <summary>The process name (for the icon) and the program's own description ("Google Chrome") of a window's owner.</summary>
    private static (string Process, string App) Owner(uint pid)
    {
        var now = Environment.TickCount64;
        lock (Owners)
        {
            if (Owners.TryGetValue(pid, out var known) && now - known.At < 60_000) return (known.Process, known.App);
            if (Owners.Count > 400) Owners.Clear();
        }
        string process = "", app = "";
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            process = p.ProcessName;
            app = process;
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var buf = new char[1024];
                    var size = (uint)buf.Length;
                    if (QueryFullProcessImageNameW(handle, 0, buf, ref size))
                    {
                        var desc = FileVersionInfo.GetVersionInfo(new string(buf, 0, (int)size)).FileDescription;
                        if (!string.IsNullOrWhiteSpace(desc)) app = desc.Trim();
                    }
                }
                finally { CloseHandle(handle); }
            }
        }
        catch { /* gone, or not ours to read */ }
        lock (Owners) Owners[pid] = (process, app, now);
        return (process, app);
    }

    // ---- monitors, in physical pixels (the calls run per-monitor DPI aware), the main one first
    private static List<(RECT R, bool Primary)> MonitorRects()
    {
        var list = new List<(RECT R, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, ref _, _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(hMon, ref mi)) list.Add((mi.rcMonitor, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.R.Left).ThenBy(m => m.R.Top).ToList();
    }

    private static readonly IntPtr PerMonitorAwareV2 = new(-4);
    private const int GWL_EXSTYLE = -20, GW_OWNER = 4, SW_MINIMIZE = 6, SW_MAXIMIZE = 3, SW_RESTORE = 9, DWMWA_CLOAKED = 14;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    private const uint WM_CLOSE = 0x10, WM_SETTINGCHANGE = 0x1A, SMTO_ABORTIFHUNG = 2, MONITORINFOF_PRIMARY = 1, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr data);
    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, [Out] char[] text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, [Out] char[] text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, [Out] char[] name, ref uint size);
#endif
}
