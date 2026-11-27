using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>RokeBox's PC spec sheet, health check, data usage and power timer (RokeSystem.cs).</summary>
public sealed class RokeSystemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rokebox-system-" + Guid.NewGuid().ToString("N"));

    public RokeSystemTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ConfigStore Config(string widgets, bool allowPower = false)
    {
        var path = Path.Combine(_dir, "config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, $$$"""{"controls":{"allowPower":{{{(allowPower ? "true" : "false")}}}},"pages":[{"id":"p","title":"P","enabled":true,"widgets":[{{{widgets}}}]}]}""");
        return new ConfigStore(path);
    }

    private static string W(string id, string type, string options = "{}") => $$"""{"id":"{{id}}","type":"{{type}}","x":0,"y":0,"w":6,"h":6,"options":{{options}}}""";

    // ---------------------------------------------------------------- spec sheet

    /// <summary>A raw SMBIOS table (Windows' 8-byte header, then structures) with one DDR5 module and one empty slot.</summary>
    private static byte[] Smbios()
    {
        var body = new List<byte>();
        var dimm = new byte[0x28];
        dimm[0] = 17; dimm[1] = 0x28;
        BitConverter.GetBytes((ushort)8192).CopyTo(dimm, 0x0C); // 8 GB in MB
        dimm[0x10] = 1;                                          // device locator: string 1
        dimm[0x12] = 0x22;                                       // DDR5
        BitConverter.GetBytes((ushort)5600).CopyTo(dimm, 0x15);  // rated speed
        dimm[0x17] = 2;                                          // manufacturer: string 2
        dimm[0x1A] = 3;                                          // part number: string 3
        BitConverter.GetBytes((ushort)4800).CopyTo(dimm, 0x20);  // configured speed
        body.AddRange(dimm);
        body.AddRange(Encoding.ASCII.GetBytes("DIMM A\0" + "80AD000080AD\0" + "HMCG66MEBSA092N\0\0"));
        var empty = new byte[0x28];
        empty[0] = 17; empty[1] = 0x28; // size 0: an empty slot
        body.AddRange(empty);
        body.AddRange(new byte[] { 0, 0 });
        body.AddRange(new byte[] { 127, 4, 0, 0, 0, 0 }); // end of table
        var raw = new byte[8 + body.Count];
        raw[1] = 3;
        BitConverter.GetBytes(body.Count).CopyTo(raw, 4);
        body.CopyTo(raw, 8);
        return raw;
    }

    [Fact]
    public void MemoryModulesComeFromSmbiosWithTheirConfiguredSpeedAndMaker()
    {
        var module = Assert.Single(SystemInfo.MemoryModules(Smbios()));
        Assert.Equal("DIMM A", module.Slot);
        Assert.Equal(8, module.SizeGb);
        Assert.Equal("DDR5", module.Type);
        Assert.Equal(4800, module.SpeedMts); // what it runs at, not its rated 5600
        Assert.Equal("SK hynix", module.Maker);
        Assert.Equal("HMCG66MEBSA092N", module.Part);
        // a table cut short or empty is not a crash
        Assert.Empty(SystemInfo.MemoryModules(Smbios()[..20]));
        Assert.Empty(SystemInfo.MemoryModules([]));
    }

    [Theory]
    [InlineData("80AD000080AD", "SK hynix")]
    [InlineData("AD00000000000000", "SK hynix")]
    [InlineData("80CE000080CE", "Samsung")]
    [InlineData("Kingston", "Kingston")]
    [InlineData("1234567890", null)] // a code nobody knows says nothing
    public void MemoryMakersAreNamedFromTheirJedecCode(string raw, string? maker) => Assert.Equal(maker, SystemInfo.MemoryMaker(raw));

    [Fact]
    public void Windows11IsNamedSoThoughTheRegistrySaysWindows10()
    {
        Assert.Equal("Windows 11 Pro", SystemInfo.OsName("Windows 10 Pro", "26300"));
        Assert.Equal("Windows 10 Pro", SystemInfo.OsName("Windows 10 Pro", "19045"));
        Assert.Equal("Windows", SystemInfo.OsName(null, "26300"));
    }

    [Theory]
    [InlineData("Dell Inc.", "Dell G15 5530", "Dell G15 5530")]
    [InlineData("LENOVO", "LENOVO 82JW", "LENOVO 82JW")]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG Strix G513", "ASUSTeK COMPUTER INC. ROG Strix G513")]
    [InlineData(null, "Desk PC", "Desk PC")]
    [InlineData("HP", null, "HP")]
    public void TheSystemNameDoesNotRepeatItsMaker(string? maker, string? product, string expected) =>
        Assert.Equal(expected, SystemInfo.SystemName(maker, product));

    [Fact]
    public void DiskBusesHaveTheirNames()
    {
        Assert.Equal("NVMe", SystemInfo.Bus(17));
        Assert.Equal("SATA", SystemInfo.Bus(11));
        Assert.Equal("USB", SystemInfo.Bus(7));
        Assert.Null(SystemInfo.Bus(99));
    }

    [Fact]
    public void TheSpecSheetIsReadOnlyForASysinfoWidget()
    {
        Assert.False(new SystemInfo(Config(W("c", "clock")), NullLogger<SystemInfo>.Instance).View().Enabled);
        var view = new SystemInfo(Config(W("i", "sysinfo")), NullLogger<SystemInfo>.Instance).View();
        Assert.True(view.Enabled);
        Assert.NotNull(view.Cpu);
        Assert.Equal(Environment.ProcessorCount, view.Cpu!.Threads);
        Assert.NotNull(view.Os);
        Assert.True(view.Os!.BootedAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    // ---------------------------------------------------------------- network adapters

    [Theory]
    [InlineData("Ethernet-WFP Native MAC Layer LightWeight Filter-0000", "Realtek PCIe GbE Family Controller-WFP Native MAC Layer LightWeight Filter-0000", true)]
    [InlineData("Ethernet-Kaspersky Lab NDIS 6 Filter-0000", "Realtek PCIe GbE Family Controller-Kaspersky Lab NDIS 6 Filter-0000", true)]
    [InlineData("Ethernet-QoS Packet Scheduler-0000", "Realtek PCIe GbE Family Controller-QoS Packet Scheduler-0000", true)]
    [InlineData("Wi-Fi-WFP 802.3 MAC Layer LightWeight Filter-0000", "Intel(R) Wi-Fi 6E AX211 160MHz-WFP 802.3 MAC Layer LightWeight Filter-0000", true)]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", false)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6E AX211 160MHz", false)]
    [InlineData("Ethernet 2", "Intel(R) Ethernet Connection (7) I219-V", false)]
    public void FilterInstancesOfAnAdapterAreNotCountedAgain(string name, string description, bool filter)
    {
        Assert.Equal(filter, SystemMonitor.IsFilterInstance(name, description));
        Assert.Equal(!filter, SystemMonitor.Counted(name, description, NetworkInterfaceType.Ethernet, []));
    }

    // ---------------------------------------------------------------- health check

    [Fact]
    public void HealthLevelsWarnBeforeTheyAreBad()
    {
        Assert.Equal("ok", PcHealth.Level(40, 85, 95));
        Assert.Equal("warn", PcHealth.Level(90, 85, 95));
        Assert.Equal("bad", PcHealth.Level(97, 85, 95));
        // free space: lower is worse
        Assert.Equal("ok", PcHealth.Level(40, 15, 5, higherIsWorse: false));
        Assert.Equal("warn", PcHealth.Level(12, 15, 5, higherIsWorse: false));
        Assert.Equal("bad", PcHealth.Level(3, 15, 5, higherIsWorse: false));
    }

    [Fact]
    public void TheRecycleBinIsEmptiedOnlyForAWidgetThatAllowsIt()
    {
        var emptied = 0;
        var off = new PcHealth(Config(W("h", "pchealth")), NullLogger<PcHealth>.Instance) { Read = () => [], Empty = () => { emptied++; return true; } };
        Assert.False(off.View(null, "p/h").CanClean);
        Assert.NotNull(off.Clean(null, "p/h"));
        Assert.Equal(0, emptied);

        var on = new PcHealth(Config(W("h", "pchealth", """{"allowClean":true}""") + "," + W("x", "pchealth")), NullLogger<PcHealth>.Instance)
        { Read = () => [new HealthCheck("ram", "ok", null, 40, 6, 16)], Empty = () => { emptied++; return true; } };
        Assert.True(on.View(null, "p/h").CanClean);
        Assert.False(on.View(null, "p/x").CanClean); // another widget's option does not count
        Assert.NotNull(on.Clean(null, "p/x"));
        Assert.NotNull(on.Clean(null, null));
        Assert.Equal(0, emptied);
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(on.Clean(null, "p/h"));
        Assert.Equal(1, emptied);
    }

    // ---------------------------------------------------------------- data usage

    [Fact]
    public void DataUsageAddsUpEachDayAcrossCounterResetsAndIsKept()
    {
        var file = Path.Combine(_dir, "data-usage.json");
        var config = Config(W("d", "datausage"));
        var now = new DateTime(2026, 11, 26, 23, 59, 50);
        var counters = new Dictionary<string, (long Rx, long Tx)> { ["eth"] = (1_000_000, 200_000) };
        var usage = new DataUsageMonitor(config, NullLogger<DataUsageMonitor>.Instance)
        { FilePath = file, Now = () => now, Counters = _ => new(counters) };

        usage.Tick(); // the first reading is the baseline
        Assert.Empty(usage.View().Days);
        counters["eth"] = (3_000_000, 700_000);
        usage.Tick();
        var day = Assert.Single(usage.View().Days);
        Assert.Equal(("2026-11-26", 2_000_000L, 500_000L), (day.Date, day.Down, day.Up));
        Assert.Equal("2026-11-26", usage.View().Since);

        // midnight: the next bytes are tomorrow's; the adapter was reset, so its counter starts again from 0
        now = now.AddSeconds(20);
        counters["eth"] = (400_000, 100_000);
        usage.Tick();
        Assert.Equal(["2026-11-26", "2026-11-27"], usage.View().Days.Select(d => d.Date));
        Assert.Equal(400_000, usage.View().Days[1].Down);

        // a new adapter counts from its own next reading, not with what it moved before
        counters["wifi"] = (9_000_000_000, 1);
        usage.Tick();
        Assert.Equal(400_000, usage.View().Days[1].Down);

        usage.Save();
        var again = new DataUsageMonitor(config, NullLogger<DataUsageMonitor>.Instance) { FilePath = file, Now = () => now };
        Assert.Equal(usage.View().Days, again.View().Days);
        Assert.Equal("2026-11-26", again.View().Since);
        Assert.False(new DataUsageMonitor(Config(W("c", "clock")), NullLogger<DataUsageMonitor>.Instance) { FilePath = file }.View().Enabled);
    }

    // ---------------------------------------------------------------- power timer

    [Fact]
    public void ThePowerTimerNeedsTheDesktopOptInItsWidgetAndAFixedActionAndBoundedMinutes()
    {
        var calls = new List<string>();
        var off = new PowerTimer(Config(W("t", "powertimer")), NullLogger<PowerTimer>.Instance) { Shutdown = a => calls.Add(string.Join(' ', a)) };
        Assert.Equal("off", off.Start(null, "p/t", "shutdown", 30));
        Assert.False(off.View().Allowed);

        var timer = new PowerTimer(Config(W("t", "powertimer"), allowPower: true), NullLogger<PowerTimer>.Instance)
        { Shutdown = a => calls.Add(string.Join(' ', a)), NowMs = () => 1_000_000 };
        Assert.Equal("widget", timer.Start(null, "p/nope", "shutdown", 30));
        Assert.Equal("invalid", timer.Start(null, "p/t", "format c:", 30));
        Assert.Equal("invalid", timer.Start(null, "p/t", "shutdown", 0));
        Assert.Equal("invalid", timer.Start(null, "p/t", "shutdown", 721));
        Assert.Empty(calls);
        if (!OperatingSystem.IsWindows()) return;

        Assert.Null(timer.Start(null, "p/t", "shutdown", 30));
        Assert.Equal(["/s /t 1800"], calls);
        var view = timer.View();
        Assert.Equal(("shutdown", 1_000_000 + 1_800_000L), (view.Action, view.EndsAt));
        // a new timer replaces the old one: Windows' shutdown is called off first
        Assert.Null(timer.Start(null, "p/t", "restart", 90));
        Assert.Equal(["/s /t 1800", "/a", "/r /t 5400"], calls);
        Assert.Null(timer.Cancel(null, "p/t"));
        Assert.Equal("/a", calls[^1]);
        Assert.Null(timer.View().Action);
        // sleep is the agent's own timer: nothing goes to shutdown.exe
        var count = calls.Count;
        Assert.Null(timer.Start(null, "p/t", "sleep", 15));
        Assert.Equal(count, calls.Count);
        Assert.Equal("sleep", timer.View().Action);
        Assert.Null(timer.Cancel(null, "p/t"));
        Assert.Equal(count, calls.Count);
    }
}
