using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>RokeBox's Gaming widget: the running Steam game, what its processes use, and the session's network.</summary>
public sealed class GamingMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rokebox-gaming-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_dir, "Steam");
    private string Other => Path.Combine(_dir, "Games");

    public GamingMonitorTests()
    {
        Directory.CreateDirectory(Path.Combine(Root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(Other, "steamapps"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Acf(string id, string name, string installdir, long size = 64_300_000_000, long lastPlayed = 0) => $$"""
        "AppState"
        {
        	"appid"		"{{id}}"
        	"name"		"{{name}}"
        	"StateFlags"		"4"
        	"installdir"		"{{installdir}}"
        	"LastPlayed"		"{{lastPlayed}}"
        	"SizeOnDisk"		"{{size}}"
        	"LastUpdated"		"1795000000"
        }
        """;

    private void Manifest(string folder, string id, string name, long lastPlayed = 0, long size = 64_300_000_000) =>
        File.WriteAllText(Path.Combine(folder, "steamapps", $"appmanifest_{id}.acf"), Acf(id, name, name, size, lastPlayed));

    private void LibraryFolders() => File.WriteAllText(Path.Combine(Root, "steamapps", "libraryfolders.vdf"), $$"""
        "libraryfolders"
        {
        	"0" { "path" "{{Root.Replace("\\", "\\\\")}}" }
        	"1" { "path" "{{Other.Replace("\\", "\\\\")}}" }
        }
        """);

    private ConfigStore Config(string widget = "gaming")
    {
        var path = Path.Combine(_dir, "config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, $$$"""{"pages":[{"id":"p","title":"P","enabled":true,"widgets":[{"id":"w","type":"{{{widget}}}","x":0,"y":0,"w":12,"h":6}]}]}""");
        return new ConfigStore(path);
    }

    private SteamLibrary Steam(ConfigStore config, long running) =>
        new(config, NullLogger<SteamLibrary>.Instance) { FindRoot = () => Root, RunningAppId = _ => running, StartProcess = _ => { } };

    // ---------------------------------------------------------------- Steam's appmanifest

    [Fact]
    public void TheManifestGivesNameFolderAndSizeAndNeverAFolderOutsideCommon()
    {
        var apps = Path.Combine(Root, "steamapps");
        var info = GamingMonitor.ParseManifest(Acf("730", "Counter-Strike 2", "Counter-Strike Global Offensive"), apps);
        Assert.NotNull(info);
        Assert.Equal("Counter-Strike 2", info!.Name);
        Assert.Equal(Path.Combine(apps, "common", "Counter-Strike Global Offensive"), info.InstallPath);
        Assert.Equal(64_300_000_000, info.SizeOnDisk);
        Assert.Equal(1795000000, info.LastUpdated);
        // an installdir that would leave steamapps/common is not trusted
        Assert.Null(GamingMonitor.ParseManifest(Acf("1", "x", ".."), apps));
        Assert.Null(GamingMonitor.ParseManifest(Acf("1", "x", "..\\\\Windows"), apps));
        Assert.Null(GamingMonitor.ParseManifest(Acf("1", "x", "a/b"), apps));
        Assert.Null(GamingMonitor.ParseManifest(Acf("1", "x", ""), apps));
        Assert.Null(GamingMonitor.ParseManifest("not a manifest", apps));
    }

    [Fact]
    public void TheManifestIsFoundInAnyLibraryFolder()
    {
        LibraryFolders();
        Manifest(Other, "900010", "Harbor Lights");
        var info = GamingMonitor.FindManifest(Root, "900010");
        Assert.Equal(Path.Combine(Other, "steamapps", "common", "Harbor Lights"), info?.InstallPath);
        Assert.Null(GamingMonitor.FindManifest(Root, "900099"));
        Assert.Null(GamingMonitor.FindManifest(Root, "../1"));
        Assert.Null(GamingMonitor.FindManifest(null, "900010"));
    }

    // ---------------------------------------------------------------- GPU per process

    [Fact]
    public void TheGamesGpuLoadIsItsBusiestEngineSummedOverItsProcesses()
    {
        var engines = new List<(string, double)>
        {
            ("pid_100_luid_0x00000000_0x0000D1A7_phys_0_eng_0_engtype_3D", 61.5),
            ("pid_101_luid_0x00000000_0x0000D1A7_phys_0_eng_0_engtype_3D", 20),   // the game's second process, same engine
            ("pid_100_luid_0x00000000_0x0000D1A7_phys_0_eng_3_engtype_VideoDecode", 12),
            ("pid_555_luid_0x00000000_0x0000D1A7_phys_0_eng_0_engtype_3D", 15),   // another program
            ("garbage", 99),
        };
        Assert.Equal(81.5, GamingMonitor.GameGpu(engines, new HashSet<int> { 100, 101 }));
        Assert.Equal(0, GamingMonitor.GameGpu(engines, new HashSet<int> { 777 }));    // processes that use no engine
        Assert.Null(GamingMonitor.GameGpu(engines, new HashSet<int>()));              // no processes: unknown
        // never above 100 %, even when two processes overlap on one engine
        Assert.Equal(100, GamingMonitor.GameGpu([("pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 70), ("pid_2_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 70)], new HashSet<int> { 1, 2 }));
    }

    [Fact]
    public void TheGamesVideoMemoryIsSummedOverItsProcesses()
    {
        var memory = new List<(string, double)>
        {
            ("pid_100_luid_0x00000000_0x0000D1A7_phys_0", 5.5e9),
            ("pid_101_luid_0x00000000_0x0000D1A7_phys_0", 0.75e9),
            ("pid_555_luid_0x00000000_0x0000D1A7_phys_0", 3e9),
            ("pid_100_luid_0x00000000_0x0000D1A7_phys_0_eng_0_engtype_3D", 9e9), // an engine instance, not memory
        };
        Assert.Equal(6.25, GamingMonitor.GameVram(memory, new HashSet<int> { 100, 101 }));
        Assert.Null(GamingMonitor.GameVram(memory, new HashSet<int>()));
    }

    // ---------------------------------------------------------------- processes

    [Fact]
    public void AProgramUnderTheGamesFolderIsTheGamesButNotOneInASiblingFolder()
    {
        var dir = Path.Combine(Root, "steamapps", "common", "Game") + Path.DirectorySeparatorChar;
        Assert.True(GameProcesses.IsUnder(Path.Combine(Root, "steamapps", "common", "Game", "bin", "game.exe"), dir));
        Assert.False(GameProcesses.IsUnder(Path.Combine(Root, "steamapps", "common", "Game2", "game.exe"), dir));
        Assert.False(GameProcesses.IsUnder(Path.Combine(Root, "steam.exe"), dir));
    }

    [Fact]
    public void AProcessIsMeasuredBetweenTwoReadings()
    {
        var procs = new GameProcesses();
        var first = procs.Sample([Environment.ProcessId], 1000);
        Assert.Equal(1, first.Count);
        Assert.True(first.RamGb > 0);
        Assert.Null(first.Cpu); // a rate needs two readings
        var until = Environment.TickCount64 + 150;
        var spin = 0L;
        while (Environment.TickCount64 < until) spin++;
        var second = procs.Sample([Environment.ProcessId], 1000 + 200);
        Assert.NotNull(second.Cpu);
        Assert.InRange(second.Cpu!.Value, 0, 100);
        Assert.NotNull(second.StartedAt);
        Assert.Equal(0, procs.Sample([], 1500).Count);
        Assert.True(spin > 0);
    }

    // ---------------------------------------------------------------- the widget's view

    [Fact]
    public void NothingIsReadWithoutAGamingWidgetOnAnEnabledPage()
    {
        var config = Config("clock");
        var gaming = new GamingMonitor(config, Steam(config, 900010), NullLogger<GamingMonitor>.Instance);
        var view = gaming.View();
        Assert.False(view.Enabled);
        Assert.Null(view.Game);
    }

    [Fact]
    public void TheRunningGameComesWithItsSizeDriveAndTheSessionsNetwork()
    {
        LibraryFolders();
        Manifest(Other, "900010", "Harbor Lights", lastPlayed: 1_795_000_000);
        Manifest(Root, "900020", "Starfall Tactics", lastPlayed: 1_794_000_000, size: 12_000_000_000);
        Manifest(Root, "900030", "Moss Garden", lastPlayed: 1_795_500_000, size: 3_000_000_000);
        var config = Config();
        long clock = 10_000, rx = 5_000_000, tx = 1_000_000;
        var gaming = new GamingMonitor(config, Steam(config, 900010), NullLogger<GamingMonitor>.Instance)
        {
            Clock = () => clock, NetTotals = _ => (rx, tx), FindProcesses = _ => [],
        };

        var view = gaming.View();
        Assert.True(view.Enabled);
        Assert.True(view.Found);
        Assert.Equal(3, view.Installed);
        var game = Assert.IsType<GamingGame>(view.Game);
        Assert.Equal("900010", game.Id);
        Assert.Equal("Harbor Lights", game.Name);
        Assert.Equal(64.3, game.SizeGb);
        Assert.Equal(0, game.Processes);
        Assert.Null(game.Cpu);
        Assert.Null(game.NetDownBps);    // a rate needs two readings
        Assert.Equal(0, game.SessionDownMb);
        Assert.NotNull(game.StartedAt);  // when RokeBox first saw it, without processes
        // the recently played games leave out the one being played, most recent first
        Assert.Equal(["900030", "900020"], view.Recent.Select(r => r.Id));

        // two seconds later: 2 MB down and 0.5 MB up since the game started
        clock += 2000;
        rx += 2_000_000;
        tx += 500_000;
        game = gaming.View().Game!;
        Assert.Equal(1_000_000, game.NetDownBps);
        Assert.Equal(250_000, game.NetUpBps);
        Assert.Equal(2.0, game.SessionDownMb);
        Assert.Equal(0.5, game.SessionUpMb);

        // within a second the reading is shared, not taken again
        rx += 9_000_000;
        clock += 100;
        Assert.Equal(2.0, gaming.View().Game!.SessionDownMb);
    }

    [Fact]
    public void WithoutAGameTheViewListsTheRecentlyPlayedGamesAndTheLibrarySize()
    {
        Manifest(Root, "900020", "Starfall Tactics", lastPlayed: 1_794_000_000, size: 12_000_000_000);
        Manifest(Root, "900030", "Moss Garden", lastPlayed: 1_795_500_000, size: 3_000_000_000);
        Manifest(Root, "900040", "Never Played", lastPlayed: 0, size: 1_000_000_000);
        var config = Config();
        var view = new GamingMonitor(config, Steam(config, 0), NullLogger<GamingMonitor>.Instance) { NetTotals = _ => null }.View();
        Assert.Null(view.Game);
        Assert.Equal(["900030", "900020"], view.Recent.Select(r => r.Id));
        Assert.Equal(16.0, view.LibraryGb);
    }

    [Fact]
    public void WithoutSteamTheViewSaysSo()
    {
        var config = Config();
        var steam = new SteamLibrary(config, NullLogger<SteamLibrary>.Instance) { FindRoot = () => null, RunningAppId = _ => 0, StartProcess = _ => { } };
        var view = new GamingMonitor(config, steam, NullLogger<GamingMonitor>.Instance).View();
        Assert.True(view.Enabled);
        Assert.False(view.Found);
        Assert.NotNull(view.Error);
    }

    [Fact]
    public void TheSteamLibraryServesCoversToTheGamingWidgetToo()
    {
        var config = Config();
        Assert.True(Steam(config, 0).Wanted());
    }
}
