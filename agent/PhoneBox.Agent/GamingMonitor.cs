using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

// RokeBox: the Gaming widget. The Steam game being played with what its own processes use (CPU, GPU, video memory,
// RAM, disk), its size on disk, and the computer's network while it runs. Windows cannot attribute network traffic to
// one process without administrator rights, so the network figures are the whole computer's (the adapters the network
// meters count), with the data used since the game started. Nothing is read while no Gaming widget is on an enabled page.

/// <summary>The game being played and what it uses now. Rates are per second; null = not measured (yet).</summary>
public sealed record GamingGame(
    string Id, string Name, double? SizeGb, string? Drive, double? DriveFreeGb, long? StartedAt, int Processes,
    double? Cpu, double? Gpu, double? VramGb, double? RamGb, double? DiskReadBps, double? DiskWriteBps,
    double? NetDownBps, double? NetUpBps, double? SessionDownMb, double? SessionUpMb, long LastUpdated, bool Cover);

/// <summary>A recently played installed game (the widget's list while nothing runs).</summary>
public sealed record GamingRecent(string Id, string Name, double SizeGb, long LastPlayed, bool Cover);

/// <summary>What the Gaming widget shows: the game being played, if any, and the library around it.</summary>
public sealed record GamingView(bool Enabled, bool Found, string? Error, GamingGame? Game, IReadOnlyList<GamingRecent> Recent,
    int Installed, double LibraryGb);

public sealed partial class GamingMonitor(ConfigStore config, SteamLibrary steam, ILogger<GamingMonitor> log) : IDisposable
{
    /// <summary>The widget asks every 2 s; several phones share one reading.</summary>
    internal const int MinSampleMs = 1000;

    private readonly Lock _gate = new();
    private GamingView? _last;
    private long _lastAt;
    private string? _root;
    private long _rootAt = -(1L << 40);
    private (string Id, ManifestInfo? Info)? _manifest;
    private string? _sessionId;
    private long _sessionSeen;
    private (long Rx, long Tx)? _sessionBase;
    private (long Rx, long Tx, long At)? _netPrev;
    private readonly GameProcesses _procs = new();
    private GpuByProcess? _gpu;

    /// <summary>Overridable for tests: the clock (ms).</summary>
    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;
    /// <summary>Overridable for tests: the network meters' adapters, bytes received and sent since they came up.</summary>
    internal Func<IReadOnlyList<string>, (long Rx, long Tx)?> NetTotals { get; init; } = ReadNetTotals;
    /// <summary>Overridable for tests: the processes whose program lies under the game's folder.</summary>
    internal Func<string, IReadOnlyList<int>>? FindProcesses { get; init; }

    public bool Wanted() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "gaming");

    public GamingView View()
    {
        if (!Wanted()) return new GamingView(false, false, null, null, [], 0, 0);
        lock (_gate)
        {
            var now = Clock();
            if (_last is not null && now - _lastAt < MinSampleMs) return _last;
            try { _last = Build(now); }
            catch (Exception ex)
            {
                log.LogWarning("gaming: {Msg}", ex.Message);
                _last = new GamingView(true, false, "Could not read the game: " + ex.Message, null, [], 0, 0);
            }
            _lastAt = now;
            return _last;
        }
    }

    private GamingView Build(long now)
    {
        var lib = steam.View();
        if (!lib.Found)
        {
            EndSession();
            return new GamingView(true, false, lib.Error, null, [], 0, 0);
        }
        var games = lib.Games;
        var recent = games.Where(g => g.LastPlayed > 0 && g.Id != lib.Running).OrderByDescending(g => g.LastPlayed).Take(6)
            .Select(g => new GamingRecent(g.Id, g.Name, g.SizeGb, g.LastPlayed, g.Cover)).ToList();
        var libraryGb = Math.Round(games.Sum(g => g.SizeGb), 1);
        GamingGame? game = null;
        if (lib.Running is { } id) game = Sample(id, games.FirstOrDefault(g => g.Id == id), now);
        else EndSession();
        return new GamingView(true, true, lib.Error, game, recent, games.Count, libraryGb);
    }

    private GamingGame Sample(string id, SteamGame? known, long now)
    {
        if (_sessionId != id)
        {
            // a new game: its network session and process history start now
            _sessionId = id;
            _sessionSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _sessionBase = NetTotals(Adapters());
            _netPrev = null;
            _procs.Reset();
        }
        var info = Manifest(id);
        IReadOnlyList<int> pids = info is null ? [] : FindProcesses is { } find ? find(info.InstallPath) : _procs.Find(info.InstallPath);
        var use = _procs.Sample(pids, now);
        double? gpu = null, vram = null;
        if (OperatingSystem.IsWindows() && pids.Count > 0)
        {
            _gpu ??= new GpuByProcess();
            (gpu, vram) = _gpu.Sample(pids);
        }

        double? down = null, up = null, sessionDown = null, sessionUp = null;
        if (NetTotals(Adapters()) is { } total)
        {
            if (_netPrev is { } prev && now > prev.At)
            {
                var secs = (now - prev.At) / 1000.0;
                down = Math.Max(0, (total.Rx - prev.Rx) / secs);
                up = Math.Max(0, (total.Tx - prev.Tx) / secs);
            }
            _netPrev = (total.Rx, total.Tx, now);
            _sessionBase ??= total;
            sessionDown = Math.Round(Math.Max(0, total.Rx - _sessionBase.Value.Rx) / 1e6, 1);
            sessionUp = Math.Round(Math.Max(0, total.Tx - _sessionBase.Value.Tx) / 1e6, 1);
        }

        string? drive = null;
        double? free = null;
        if (info is not null)
        {
            try
            {
                if (Path.GetPathRoot(info.InstallPath) is { Length: > 0 } root)
                {
                    var d = new DriveInfo(root);
                    // Steam may keep the library path in lower case ("c:/program files (x86)/steam"): show "C:"
                    drive = OperatingSystem.IsWindows() ? d.Name.TrimEnd('\\', '/').ToUpperInvariant() : d.Name;
                    free = Math.Round(d.AvailableFreeSpace / 1e9, 1);
                }
            }
            catch { } // a drive that went away or a network path
        }

        var size = known is { SizeGb: > 0 } ? known.SizeGb : info is { SizeOnDisk: > 0 } ? Math.Round(info.SizeOnDisk / 1e9, 1) : (double?)null;
        var name = known is { Name.Length: > 0 } ? known.Name : info is { Name.Length: > 0 } ? info.Name : "App " + id;
        return new GamingGame(id, name, size, drive, free, use.StartedAt ?? _sessionSeen, use.Count,
            use.Cpu, gpu, vram, use.RamGb, use.ReadBps, use.WriteBps, down, up, sessionDown, sessionUp,
            info?.LastUpdated ?? 0, known?.Cover ?? false);
    }

    private void EndSession()
    {
        _sessionId = null;
        _sessionBase = null;
        _netPrev = null;
    }

    private IReadOnlyList<string> Adapters() => config.Get().Ping.Adapters;

    public const string CloseOff = "Turn on \"Let the phone close the game\" in this widget's settings";

    /// <summary>Whether the asking Gaming widget, on an enabled page of this phone, lets the phone close the game.</summary>
    public bool CloseAllowed(string? deviceId, string? widget) =>
        PanelWidgets.Find(config.Get(), deviceId, widget, "gaming")?.Bool("allowClose") == true;

    /// <summary>
    /// Closes the Steam game being played, for the asking widget with "Let the phone close the game" on (off by default).
    /// Normally its windows are asked to close, as their X button does, so the game can save; <paramref name="force"/>
    /// ends its processes, for a game that stopped answering (unsaved progress is lost). Only the processes under the
    /// game's own install folder: never Steam or anything else.
    /// </summary>
    public (int Status, string? Error, int Count) CloseGame(string? deviceId, string? widget, bool force)
    {
        if (PanelWidgets.Find(config.Get(), deviceId, widget, "gaming") is not { } w)
            return (404, "this Gaming widget isn't on an enabled page of this phone", 0);
        if (w.Bool("allowClose") != true) return (403, CloseOff, 0);
        if (steam.View().Running is not { } id) return (409, "no game is running", 0);
        IReadOnlyList<int> pids;
        lock (_gate)
        {
            var info = Manifest(id);
            pids = info is null ? [] : FindProcesses is { } find ? find(info.InstallPath) : _procs.Find(info.InstallPath);
        }
        if (pids.Count == 0) return (409, "the game's processes were not found", 0);
        var done = 0;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (force) { p.Kill(); done++; }
                else if (p.CloseMainWindow()) done++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        if (done == 0)
            return (409, force ? "Windows did not let the agent end the game (it may run as administrator)"
                : "the game has no window that can be asked to close: use Force close", 0);
        log.LogInformation("gaming: {How} the game from the phone ({N} processes)", force ? "ended" : "asked to close", done);
        return (200, null, done);
    }

    private ManifestInfo? Manifest(string id)
    {
        if (_manifest is { } m && m.Id == id) return m.Info;
        ManifestInfo? info = null;
        try { info = FindManifest(Root(), id); }
        catch (Exception ex) { log.LogDebug("gaming: manifest of {Id}: {Msg}", id, ex.Message); }
        _manifest = (id, info);
        return info;
    }

    private string? Root()
    {
        var now = Clock();
        if (now - _rootAt > 60_000) { _root = steam.FindRoot(); _rootAt = now; }
        return _root;
    }

    // ---------------------------------------------------------------- Steam's appmanifest (name, folder, size)

    internal sealed record ManifestInfo(string Name, string InstallPath, long SizeOnDisk, long LastUpdated);

    /// <summary>The game's appmanifest_&lt;id&gt;.acf in any library folder, or null.</summary>
    internal static ManifestInfo? FindManifest(string? root, string id)
    {
        if (root is null || !SteamLibrary.IsAppId(id)) return null;
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) vdf = Path.Combine(root, "config", "libraryfolders.vdf");
        var text = File.Exists(vdf) && new FileInfo(vdf).Length <= SteamLibrary.MaxManifestBytes ? File.ReadAllText(vdf) : "";
        foreach (var folder in SteamLibrary.LibraryFolders(root, text))
        {
            var apps = Path.Combine(folder, "steamapps");
            var file = Path.Combine(apps, $"appmanifest_{id}.acf");
            try
            {
                var fi = new FileInfo(file);
                if (!fi.Exists || fi.Length > SteamLibrary.MaxManifestBytes) continue;
                if (ParseManifest(File.ReadAllText(file), apps) is { } info) return info;
            }
            catch (IOException) { } // Steam is rewriting it; the next reading finds it
        }
        return null;
    }

    /// <summary>
    /// One appmanifest: the game's name, its folder (steamapps/common/&lt;installdir&gt;), size on disk and last update.
    /// An installdir that leaves steamapps/common (rooted, or with "..") is not trusted.
    /// </summary>
    internal static ManifestInfo? ParseManifest(string text, string steamapps)
    {
        var app = VdfNode.Parse(text)["AppState"];
        var dir = app?.Str("installdir");
        if (app is null || string.IsNullOrWhiteSpace(dir) || dir.Contains("..") || Path.IsPathRooted(dir)
            || dir.IndexOfAny(['/', '\\']) >= 0) return null;
        return new ManifestInfo(app.Str("name") ?? "", Path.Combine(steamapps, "common", dir), app.Long("SizeOnDisk"), app.Long("LastUpdated"));
    }

    // ---------------------------------------------------------------- network (the network meters' adapters)

    private static (long Rx, long Tx)? ReadNetTotals(IReadOnlyList<string> chosen)
    {
        try
        {
            long rx = 0, tx = 0;
            var any = false;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (!SystemMonitor.Counted(ni.Name, ni.Description, ni.NetworkInterfaceType, chosen)) continue;
                try
                {
                    var s = ni.GetIPStatistics();
                    rx += s.BytesReceived;
                    tx += s.BytesSent;
                    any = true;
                }
                catch { }
            }
            return any ? (rx, tx) : null;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- GPU per process (Windows performance counters)

    // pid_1234_luid_0x00000000_0x0001C621_phys_0_eng_3_engtype_3D  /  pid_1234_luid_0x00000000_0x0001C621_phys_0
    [GeneratedRegex(@"^pid_(\d+)_luid_0x([0-9a-f]{8})_0x([0-9a-f]{8})_phys_(\d+)(?:_eng_(\d+)_engtype_.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GpuInstance();

    /// <summary>
    /// The game's GPU load from "GPU Engine" instances: each engine's load summed over the game's processes, and the
    /// busiest engine is the load (as Task Manager shows a process). 0 when its processes use no engine; null without processes.
    /// </summary>
    internal static double? GameGpu(IEnumerable<(string Instance, double Value)> engines, IReadOnlySet<int> pids)
    {
        if (pids.Count == 0) return null;
        var perEngine = new Dictionary<string, double>();
        foreach (var (inst, v) in engines)
        {
            if (GpuInstance().Match(inst) is not { Success: true } m || !m.Groups[5].Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out var pid) || !pids.Contains(pid)) continue;
            var key = (m.Groups[2].Value + m.Groups[3].Value + "|" + m.Groups[4].Value + "|" + m.Groups[5].Value).ToLowerInvariant();
            perEngine[key] = perEngine.GetValueOrDefault(key) + v;
        }
        return Math.Round(Math.Min(100, perEngine.Count == 0 ? 0 : perEngine.Values.Max()), 1);
    }

    /// <summary>The game's dedicated video memory (GB) from "GPU Process Memory" instances; null without processes.</summary>
    internal static double? GameVram(IEnumerable<(string Instance, double Value)> memory, IReadOnlySet<int> pids)
    {
        if (pids.Count == 0) return null;
        double bytes = 0;
        foreach (var (inst, v) in memory)
            if (GpuInstance().Match(inst) is { Success: true } m && !m.Groups[5].Success
                && int.TryParse(m.Groups[1].Value, out var pid) && pids.Contains(pid)) bytes += v;
        return Math.Round(bytes / 1e9, 2);
    }

    public void Dispose() => _gpu?.Dispose();

    private sealed class GpuByProcess : IDisposable
    {
        private const uint PdhMoreData = 0x800007D2, PdhFmtDouble = 0x200, PdhFmtNoCap100 = 0x8000;
        private IntPtr _query, _engine, _memory;
        private bool _primed;

        public GpuByProcess()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
            // English counter names: the localized ones differ per Windows language
            if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engine) != 0) _engine = IntPtr.Zero;
            if (PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _memory) != 0) _memory = IntPtr.Zero;
            PdhCollectQueryData(_query); // the engine load is a rate: it needs a reading before the first answer
        }

        public (double? Gpu, double? VramGb) Sample(IReadOnlyList<int> pids)
        {
            if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return (null, null);
            var set = pids.ToHashSet();
            var vram = GameVram(Read(_memory), set);
            if (!_primed) { _primed = true; return (null, vram); }
            return (GameGpu(Read(_engine), set), vram);
        }

        private static List<(string Instance, double Value)> Read(IntPtr counter)
        {
            var list = new List<(string, double)>();
            if (counter == IntPtr.Zero) return list;
            uint size = 0, count = 0;
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, ref count, IntPtr.Zero) != PdhMoreData || size == 0) return list;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, ref count, buf) != 0) return list;
                var stride = Marshal.SizeOf<FmtItem>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<FmtItem>(buf + i * stride);
                    if (item.CStatus is not (0 or 1) || item.Name == IntPtr.Zero) continue;
                    list.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)] private struct FmtItem { public IntPtr Name; public uint CStatus; public double Value; }
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr items);
    }
}

/// <summary>What the game's processes use together. Rates per second; null until there are two readings.</summary>
internal sealed record ProcessUse(double? Cpu, double? RamGb, double? ReadBps, double? WriteBps, long? StartedAt, int Count);

/// <summary>Finds a game's processes by their program's folder and measures them between two readings.</summary>
internal sealed class GameProcesses
{
    private readonly Dictionary<int, (long Cpu, long Read, long Write, long At)> _prev = [];

    public void Reset() => _prev.Clear();

    /// <summary>The processes whose program is under <paramref name="installPath"/>.</summary>
    public IReadOnlyList<int> Find(string installPath)
    {
        var dir = installPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var found = new List<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id <= 4) continue;
                var path = PathOf(p);
                if (path is not null && IsUnder(path, dir)) found.Add(p.Id);
            }
        }
        return found;
    }

    internal static bool IsUnder(string path, string dirWithSeparator) =>
        path.StartsWith(dirWithSeparator, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sums the readings of <paramref name="pids"/>. CPU is a share of the whole processor (all cores = 100 %), like the
    /// panel's CPU tile; a process seen for the first time counts from its next reading.
    /// </summary>
    public ProcessUse Sample(IReadOnlyList<int> pids, long now)
    {
        double cpu = 0, read = 0, write = 0, ram = 0;
        long? started = null;
        var count = 0;
        var rated = false;
        var live = new HashSet<int>();
        foreach (var pid in pids)
        {
            if (Read(pid) is not { } s) continue;
            live.Add(pid);
            count++;
            ram += s.RamBytes;
            if (s.StartedAt is { } at && (started is null || at < started)) started = at;
            if (_prev.TryGetValue(pid, out var p) && now > p.At)
            {
                var secs = (now - p.At) / 1000.0;
                cpu += Math.Max(0, s.Cpu100ns - p.Cpu) / 1e7 / secs / Environment.ProcessorCount * 100;
                if (s.ReadBytes >= 0) read += Math.Max(0, s.ReadBytes - p.Read) / secs;
                if (s.WriteBytes >= 0) write += Math.Max(0, s.WriteBytes - p.Write) / secs;
                rated = true;
            }
            _prev[pid] = (s.Cpu100ns, s.ReadBytes, s.WriteBytes, now);
        }
        foreach (var gone in _prev.Keys.Where(k => !live.Contains(k)).ToList()) _prev.Remove(gone);
        if (count == 0) return new ProcessUse(null, null, null, null, null, 0);
        var hasIo = OperatingSystem.IsWindows();
        return new ProcessUse(rated ? Math.Round(Math.Min(100, cpu), 1) : null, Math.Round(ram / 1e9, 2),
            rated && hasIo ? read : null, rated && hasIo ? write : null, started, count);
    }

    private sealed record Reading(long Cpu100ns, long RamBytes, long ReadBytes, long WriteBytes, long? StartedAt);

    private static Reading? Read(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            var h = OpenProcess(QueryLimited, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                if (!GetProcessTimes(h, out var created, out _, out var kernel, out var user)) return null;
                var mem = new MemCounters { cb = (uint)Marshal.SizeOf<MemCounters>() };
                long ram = GetProcessMemoryInfo(h, ref mem, mem.cb) ? (long)mem.WorkingSetSize : 0;
                long rd = -1, wr = -1;
                if (GetProcessIoCounters(h, out var io)) { rd = (long)io.ReadTransferCount; wr = (long)io.WriteTransferCount; }
                long? start = created > 0 ? DateTimeOffset.FromFileTime(created).ToUnixTimeMilliseconds() : null;
                return new Reading(kernel + user, ram, rd, wr, start);
            }
            finally { CloseHandle(h); }
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            long? start = null;
            try { start = new DateTimeOffset(p.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds(); } catch { }
            return new Reading(p.TotalProcessorTime.Ticks, p.WorkingSet64, -1, -1, start);
        }
        catch { return null; }
    }

    private static string? PathOf(Process p)
    {
        if (OperatingSystem.IsWindows())
        {
            var h = OpenProcess(QueryLimited, false, p.Id);
            if (h == IntPtr.Zero) return null;
            try
            {
                var buf = new char[1024];
                var size = (uint)buf.Length;
                return QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
            }
            finally { CloseHandle(h); }
        }
        try { return p.MainModule?.FileName; } catch { return null; }
    }

    private const uint QueryLimited = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemCounters
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
            QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")] private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemCounters counters, uint size);
}
