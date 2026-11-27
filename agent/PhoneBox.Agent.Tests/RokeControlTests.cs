using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// RokeBox's control widgets (RokeControl.cs) act only for the widget the phone names, with that widget's own switch on,
/// and only through their fixed lists: no free key combination, window handle, action or point outside the picture.
/// The calls into Windows are replaced, so these run anywhere; what Windows alone does is checked only on Windows.
/// </summary>
public sealed class RokeControlTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rokebox-control-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_path); } catch { } }

    /// <summary>One page with each widget twice: "on" with its switches on, "off" with them off.</summary>
    private ConfigStore Config()
    {
        File.WriteAllText(_path, """
            { "pages": [ { "id": "p", "title": "P", "widgets": [
                { "id": "win", "type": "windows", "x": 0, "y": 0, "w": 3, "h": 3, "options": { "control": true } },
                { "id": "win-off", "type": "windows", "x": 3, "y": 0, "w": 3, "h": 3, "options": {} },
                { "id": "kb", "type": "keyboard", "x": 6, "y": 0, "w": 3, "h": 3, "options": { "control": true } },
                { "id": "kb-off", "type": "keyboard", "x": 9, "y": 0, "w": 3, "h": 3, "options": { "control": false } },
                { "id": "scr", "type": "screen", "x": 0, "y": 3, "w": 3, "h": 3, "options": { "view": true, "click": true } },
                { "id": "scr-view", "type": "screen", "x": 3, "y": 3, "w": 3, "h": 3, "options": { "view": true } },
                { "id": "scr-off", "type": "screen", "x": 6, "y": 3, "w": 2, "h": 3, "options": { "click": true } },
                { "id": "qa", "type": "quickactions", "x": 8, "y": 3, "w": 2, "h": 3, "options": { "control": true } },
                { "id": "qa-off", "type": "quickactions", "x": 10, "y": 3, "w": 2, "h": 3, "options": {} } ] },
              { "id": "hidden", "title": "H", "enabled": false, "widgets": [
                { "id": "kb", "type": "keyboard", "x": 0, "y": 0, "w": 3, "h": 3, "options": { "control": true } } ] } ] }
            """);
        return new ConfigStore(_path);
    }

    // ---------------------------------------------------------------- windows

    private readonly List<(long Id, string Action)> _applied = [];

    private WindowControl Windows() => new(Config(), NullLogger<WindowControl>.Instance)
    {
        List = () => [new RkWindow(42, "Notes", "Notepad", "notepad", false, false, true)],
        Apply = (id, action) => { _applied.Add((id, action)); return true; },
    };

    [Fact]
    public void WindowsAreListedButNotControlledWithTheSwitchOff()
    {
        var w = Windows();
        var (status, body) = w.View(null, "p/win-off");
        Assert.Equal(200, status);
        if (OperatingSystem.IsWindows()) Assert.False(((RkWindowsView)body).Control);
        Assert.Equal((403, WindowControl.Off), w.Act(null, "p/win-off", 42, "focus"));
        Assert.Empty(_applied);
    }

    [Fact]
    public void OnlyTheNamedWidgetOnAnEnabledPageCounts()
    {
        var w = Windows();
        Assert.Equal(404, w.View(null, "p/nope").Status);
        Assert.Equal(404, w.View(null, null).Status);
        Assert.Equal(404, w.Act(null, "p/kb", 42, "focus").Status); // a keyboard widget is not a windows widget
        Assert.Empty(_applied);
    }

    [Fact]
    public void OnlyKnownActionsOnListedWindows()
    {
        var w = Windows();
        Assert.Equal(400, w.Act(null, "p/win", 42, "destroy").Status);
        Assert.Equal(400, w.Act(null, "p/win", 42, null).Status);
        if (!OperatingSystem.IsWindows()) return;
        // a handle the list does not show (a hidden or system window) is refused
        Assert.Equal(404, w.Act(null, "p/win", 7, "close").Status);
        Assert.Equal((200, (string?)null), w.Act(null, "p/win", 42, "close"));
        Assert.Equal([(42L, "close")], _applied);
    }

    // ---------------------------------------------------------------- keyboard

    private readonly List<string> _typed = [];
    private readonly List<string> _combos = [];

    private RemoteKeys Keys() => new(Config(), NullLogger<RemoteKeys>.Instance)
    {
        TypeText = (text, _) => { _typed.Add(text); return true; },
        SendCombo = combo => { _combos.Add(string.Join("+", combo)); return true; },
        Focused = () => new FocusedApp("notepad", false),
        CheckElevation = true,
        AgentElevated = false,
    };

    [Fact]
    public async Task KeysFromTheFixedListOnly()
    {
        var k = Keys();
        Assert.Equal((200, (string?)null), await k.SendAsync(null, "p/kb", null, "copy", default));
        Assert.Equal((200, (string?)null), await k.SendAsync(null, "p/kb", null, "alttab", default));
        Assert.Equal(["ctrl+c", "alt+tab"], _combos);
        // a combination is never taken from the phone: Win+R would open Run, Ctrl+Alt+Del is not a key
        foreach (var free in new[] { "win+r", "ctrl+alt+delete", "ctrl+c", "f4", "" })
            Assert.Equal(400, (await k.SendAsync(null, "p/kb", null, free, default)).Status);
        Assert.Equal(2, _combos.Count);
    }

    [Fact]
    public async Task TextIsOneLineAndNeedsTheSwitch()
    {
        var k = Keys();
        Assert.Equal((403, RemoteKeys.Off), await k.SendAsync(null, "p/kb-off", "hi", null, default));
        Assert.Equal(404, (await k.SendAsync(null, "hidden/kb", "hi", null, default)).Status); // a disabled page
        Assert.Empty(_typed);
        await k.SendAsync(null, "p/kb", "dir\r\nformat c:", null, default);
        Assert.Equal(["dir format c:"], _typed); // a line break would be Enter
        Assert.Equal(400, (await k.SendAsync(null, "p/kb", "  \n ", null, default)).Status);
    }

    [Fact]
    public async Task NothingIsTypedIntoAnAppRunningAsAdministrator()
    {
        var k = Keys();
        k.Focused = () => new FocusedApp("cmd", true);
        Assert.Equal(409, (await k.SendAsync(null, "p/kb", "hi", null, default)).Status);
        Assert.Empty(_typed);
    }

    // ---------------------------------------------------------------- screen

    private readonly List<(int Monitor, double X, double Y, string Button)> _clicks = [];
    private long _now = 10_000;

    private ScreenView Screen() => new(Config(), NullLogger<ScreenView>.Instance)
    {
        Monitors = () => [new RkMonitor(0, 1920, 1080, true)],
        Grab = (monitor, width) => monitor == 0 ? [0xFF, 0xD8, (byte)(width / 100)] : null,
        ClickAt = (m, x, y, b) => { _clicks.Add((m, x, y, b)); return true; },
        Clock = () => _now,
    };

    [Fact]
    public void TheScreenIsShownOnlyWithItsSwitch()
    {
        var s = Screen();
        Assert.Equal((403, null, ScreenView.OffView), s.Capture(null, "p/scr-off", 0, 800));
        Assert.Equal(404, s.Capture(null, "p/kb", 0, 800).Status);
        Assert.Equal((403, ScreenView.OffView), s.Click(null, "p/scr-off", 0, 0.5, 0.5, "left")); // click alone is not enough
        Assert.Equal((403, ScreenView.OffClick), s.Click(null, "p/scr-view", 0, 0.5, 0.5, "left"));
        Assert.Empty(_clicks);
    }

    [Fact]
    public void PicturesAreSpacedAndClicksStayInThePicture()
    {
        if (!OperatingSystem.IsWindows()) return;
        var s = Screen();
        Assert.Equal(200, s.Capture(null, "p/scr", 0, 99_999).Status);
        Assert.Equal(429, s.Capture(null, "p/scr", 0, 800).Status); // a stuck loop on the phone cannot keep the CPU busy
        _now += ScreenView.MinIntervalMs;
        Assert.Equal(404, s.Capture(null, "p/scr", 3, 800).Status); // no such monitor
        foreach (var (x, y) in new[] { (-0.1, 0.5), (0.5, 1.2), (double.NaN, 0.5) })
            Assert.Equal(400, s.Click(null, "p/scr", 0, x, y, "left").Status);
        Assert.Equal(400, s.Click(null, "p/scr", 0, 0.5, 0.5, "middle").Status);
        Assert.Equal((200, (string?)null), s.Click(null, "p/scr", 0, 0.25, 0.75, "right"));
        Assert.Equal([(0, 0.25, 0.75, "right")], _clicks);
    }

    // ---------------------------------------------------------------- quick actions

    private readonly List<string> _ran = [];

    private QuickActions Quick() => new(Config(), NullLogger<QuickActions>.Instance)
    {
        SendCombo = combo => { _ran.Add(string.Join("+", combo)); return true; },
        Project = mode => { _ran.Add("project " + mode); return true; },
        IsDark = () => true,
        SetDark = dark => { _ran.Add(dark ? "dark" : "light"); return true; },
    };

    [Fact]
    public void QuickActionsAreAFixedListBehindTheSwitch()
    {
        var q = Quick();
        Assert.False(QuickActions.Known("win+r"));
        Assert.False(QuickActions.Known(null));
        Assert.True(QuickActions.Known("project-extend"));
        Assert.Equal(400, q.Run(null, "p/qa", "cmd /c del").Status);
        Assert.Equal((403, QuickActions.Off), q.Run(null, "p/qa-off", "desktop"));
        Assert.Empty(_ran);
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(200, q.Run(null, "p/qa", "project-extend").Status);
        Assert.Equal(200, q.Run(null, "p/qa", "theme-light").Status);
        Assert.Equal(200, q.Run(null, "p/qa", "clip").Status);
        Assert.Equal(["project /extend", "light", "win+alt+g"], _ran);
    }
}
