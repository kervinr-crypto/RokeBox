using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PhoneBox.Agent;

// RokeBox: more about the computer and more control over it, in four widgets.
//   sysinfo     the PC's spec sheet: Windows, CPU, memory modules (SMBIOS), board and BIOS, GPU drivers, displays,
//               physical disks, volumes and the network adapters (like AIDA64's or HWiNFO's summary)
//   pchealth    a health check (free disk space, memory, uptime, a restart Windows Update waits for, the Recycle Bin),
//               with emptying the Recycle Bin when the asking widget allows it (off by default)
//   datausage   the data the network meters' adapters move per day, kept on this computer (like GlassWire or NetWorx)
//   powertimer  sleep, shut down or restart in N minutes, behind the Control Deck's power opt-in (Settings)
// Everything is read on this computer without administrator rights, only while such a widget is on an enabled page,
// and nothing leaves it. The phone sends widget ids, fixed action names and bounded numbers, never commands or paths.

// ---------------------------------------------------------------- spec sheet (sysinfo)

public sealed record SysOs(string Name, string? Version, string Build, string? Edition, long? InstalledAt, long BootedAt, string Computer);
public sealed record SysCpu(string Name, int Cores, int Threads, int? BaseMhz);
public sealed record SysRamModule(string? Slot, double SizeGb, string? Type, int? SpeedMts, string? Maker, string? Part);
public sealed record SysRam(double TotalGb, double? UsableGb, string? Type, int? SpeedMts, IReadOnlyList<SysRamModule> Modules);
public sealed record SysBoard(string? Maker, string? Model, string? BiosVendor, string? Bios, string? BiosDate, string? System);
public sealed record SysGpu(string Name, string? Driver, string? DriverDate, double? VramGb);
public sealed record SysDisplay(int Width, int Height, int Hz, bool Primary);
public sealed record SysDisk(string Model, double SizeGb, string? Bus, bool? Ssd);
public sealed record SysVolume(string Name, string? Label, string? Format, double TotalGb, double FreeGb);
public sealed record SysNet(string Name, string Kind, long? SpeedMbps, string? Ipv4, string? Gateway, IReadOnlyList<string> Dns);
public sealed record SysInfoView(bool Enabled, SysOs? Os, SysCpu? Cpu, SysRam? Ram, SysBoard? Board, IReadOnlyList<SysGpu> Gpus,
    IReadOnlyList<SysDisplay> Displays, IReadOnlyList<SysDisk> Disks, IReadOnlyList<SysVolume> Volumes, IReadOnlyList<SysNet> Network);

public sealed partial class SystemInfo(ConfigStore config, ILogger<SystemInfo> log)
{
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    private readonly Lock _gate = new();
    private (long At, SysInfoView View)? _hardware;

    /// <summary>Overridable for tests: the clock (ms).</summary>
    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;

    public bool Wanted() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "sysinfo");

    /// <summary>The hardware is read once every 10 minutes; the volumes and the network on every call.</summary>
    public SysInfoView View()
    {
        if (!Wanted()) return new SysInfoView(false, null, null, null, null, [], [], [], [], []);
        SysInfoView hw;
        lock (_gate)
        {
            if (_hardware is not { } h || Clock() - h.At > 600_000) _hardware = (Clock(), ReadHardware());
            hw = _hardware.Value.View;
        }
        return hw with { Os = hw.Os is { } os ? os with { BootedAt = BootedAt() } : null, Volumes = Safe(Volumes, []), Network = Safe(() => Network(config.Get().Ping.Adapters), []) };
    }

    private T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex) { log.LogDebug("sysinfo: {Msg}", ex.Message); return fallback; }
    }

    private SysInfoView ReadHardware() => new(true, Safe(ReadOs, null), Safe(ReadCpu, null), Safe(ReadRam, null), Safe(ReadBoard, null),
        Safe(ReadGpus, []), Safe(ReadDisplays, []), Safe(ReadDisks, []), [], []);

    private static long BootedAt() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount64;

    // ---- Windows

    internal static SysOs ReadOs()
    {
        if (!OperatingSystem.IsWindows())
            return new SysOs(RuntimeInformation.OSDescription, null, Environment.OSVersion.Version.ToString(), null, null, BootedAt(), Environment.MachineName);
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = k?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString(CI);
        var ubr = k?.GetValue("UBR") is int u ? "." + u.ToString(CI) : "";
        long? installed = k?.GetValue("InstallDate") is int d && d != 0 ? (long)(uint)d * 1000 : null;
        return new SysOs(OsName(k?.GetValue("ProductName") as string, build), k?.GetValue("DisplayVersion") as string, build + ubr,
            k?.GetValue("EditionID") as string, installed, BootedAt(), Environment.MachineName);
    }

    /// <summary>Windows 11 still says "Windows 10" in ProductName; build 22000 and later is Windows 11.</summary>
    internal static string OsName(string? product, string build) =>
        string.IsNullOrWhiteSpace(product) ? "Windows"
        : int.TryParse(build, NumberStyles.Integer, CI, out var b) && b >= 22000 ? product.Replace("Windows 10", "Windows 11") : product;

    // ---- processor

    internal static SysCpu ReadCpu()
    {
        var name = "CPU";
        int? mhz = null;
        if (OperatingSystem.IsWindows())
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (k?.GetValue("ProcessorNameString") is string n && n.Trim().Length > 0) name = string.Join(' ', n.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (k?.GetValue("~MHz") is int m && m > 0) mhz = m;
        }
        return new SysCpu(name, PhysicalCores() ?? Environment.ProcessorCount, Environment.ProcessorCount, mhz);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Slpi { public UIntPtr Mask; public int Relationship; public long R1; public long R2; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint length);

    /// <summary>Physical cores (performance and efficiency cores alike), or null.</summary>
    private static int? PhysicalCores()
    {
        if (!OperatingSystem.IsWindows()) return null;
        uint len = 0;
        GetLogicalProcessorInformation(IntPtr.Zero, ref len);
        if (len == 0) return null;
        var buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformation(buf, ref len)) return null;
            var size = Marshal.SizeOf<Slpi>();
            var cores = 0;
            for (var off = 0; off + size <= len; off += size)
                if (Marshal.PtrToStructure<Slpi>(buf + off).Relationship == 0) cores++; // RelationProcessorCore
            return cores > 0 ? cores : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---- memory (SMBIOS type 17, read from the firmware table: no WMI, no administrator)

    internal static SysRam ReadRam()
    {
        double? usable = null;
        if (OperatingSystem.IsWindows())
        {
            var st = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref st)) usable = Math.Round(st.TotalPhys / 1073741824.0, 1);
        }
        List<SysRamModule> modules = OperatingSystem.IsWindows() && Smbios() is { } raw ? MemoryModules(raw) : [];
        var total = modules.Count > 0 ? Math.Round(modules.Sum(m => m.SizeGb), 1) : usable ?? 0;
        var type = modules.Select(m => m.Type).FirstOrDefault(t => t is not null);
        var speed = modules.Where(m => m.SpeedMts is > 0).Select(m => m.SpeedMts).DefaultIfEmpty(null).Min();
        return new SysRam(total, usable, type, speed, modules);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    [DllImport("kernel32.dll")] private static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[]? buffer, uint size);

    private static byte[]? Smbios()
    {
        const uint Rsmb = 0x52534D42; // 'RSMB'
        var size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size == 0 || size > 1 << 20) return null;
        var buf = new byte[size];
        return GetSystemFirmwareTable(Rsmb, 0, buf, size) == size ? buf : null;
    }

    /// <summary>
    /// The installed memory modules from a raw SMBIOS table (Windows' RawSMBIOSData: an 8-byte header, then the
    /// structures). Empty slots, and structures that are cut short, are skipped.
    /// </summary>
    internal static List<SysRamModule> MemoryModules(byte[] raw)
    {
        var list = new List<SysRamModule>();
        if (raw.Length < 8) return list;
        var end = Math.Min(raw.Length, 8 + Math.Max(0, BitConverter.ToInt32(raw, 4)));
        var pos = 8;
        while (pos + 4 <= end)
        {
            int type = raw[pos], len = raw[pos + 1];
            if (len < 4 || pos + len > end) break;
            // the structure's strings follow its formatted part and end with two zero bytes
            var s = pos + len;
            var stringsStart = s;
            while (s + 1 < end && !(raw[s] == 0 && raw[s + 1] == 0)) s++;
            var strings = s > stringsStart ? Encoding.ASCII.GetString(raw, stringsStart, s - stringsStart).Split('\0') : [];
            var next = s + 2;
            string? Str(int offset) => offset < len && raw[pos + offset] is > 0 and var i && i <= strings.Length ? Clean(strings[i - 1]) : null;
            int Word(int offset) => offset + 1 < len ? BitConverter.ToUInt16(raw, pos + offset) : 0;
            if (type == 17 && len >= 0x15)
            {
                var size = Word(0x0C);
                double mb = size switch
                {
                    0 or 0xFFFF => 0,
                    0x7FFF when len >= 0x20 => BitConverter.ToUInt32(raw, pos + 0x1C) & 0x7FFFFFFF,
                    _ when (size & 0x8000) != 0 => (size & 0x7FFF) / 1024.0,
                    _ => size,
                };
                if (mb > 0)
                {
                    var speed = len >= 0x22 && Word(0x20) is > 0 and < 0xFFFF and var configured ? configured : Word(0x15);
                    list.Add(new SysRamModule(Str(0x10), Math.Round(mb / 1024, 1), MemoryType(raw[pos + 0x12]),
                        speed is > 0 and < 0xFFFF ? speed : null, len >= 0x18 ? MemoryMaker(Str(0x17)) : null, len >= 0x1B ? Str(0x1A) : null));
                }
            }
            if (type == 127) break; // end of table
            pos = next;
        }
        return list;
    }

    private static string? Clean(string s)
    {
        var t = s.Trim();
        return t.Length == 0 || t is "Unknown" or "Undefined" or "Not Specified" or "NO DIMM" or "None" or "0000" ? null : t;
    }

    // JEDEC manufacturer codes as firmware writes them: the bank's continuation byte, then the code ("80AD000080AD"),
    // or the code alone in older firmware ("AD00000000000000")
    private static readonly Dictionary<string, string> Jedec = new(StringComparer.OrdinalIgnoreCase)
    {
        ["80AD"] = "SK hynix", ["AD"] = "SK hynix", ["80CE"] = "Samsung", ["CE"] = "Samsung", ["802C"] = "Micron", ["2C"] = "Micron",
        ["859B"] = "Crucial", ["0198"] = "Kingston", ["9801"] = "Kingston", ["04CD"] = "G.Skill", ["CD04"] = "G.Skill",
        ["029E"] = "Corsair", ["9E02"] = "Corsair", ["04CB"] = "ADATA", ["CB04"] = "ADATA", ["04EF"] = "Team Group", ["EF04"] = "Team Group",
        ["830B"] = "Nanya", ["0B"] = "Nanya", ["8551"] = "Qimonda",
    };

    /// <summary>A memory maker's name from its JEDEC code; a name is kept as written, an unknown code says nothing.</summary>
    internal static string? MemoryMaker(string? raw)
    {
        if (raw is null) return null;
        var hex = raw.Replace(" ", "");
        if (hex.Length < 2 || !hex.All(char.IsAsciiHexDigit)) return raw;
        if (hex.Length >= 4 && Jedec.TryGetValue(hex[..4], out var four)) return four;
        return Jedec.TryGetValue(hex[..2], out var two) ? two : null;
    }

    internal static string? MemoryType(byte t) => t switch
    {
        0x12 => "DDR", 0x13 => "DDR2", 0x18 => "DDR3", 0x1A => "DDR4", 0x1B => "LPDDR", 0x1C => "LPDDR2", 0x1D => "LPDDR3",
        0x1E => "LPDDR4", 0x22 => "DDR5", 0x23 => "LPDDR5", _ => null,
    };

    // ---- board and BIOS

    internal static SysBoard? ReadBoard()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if (k is null) return null;
        string? S(string name) => k.GetValue(name) is string v && Clean(v) is { } c && !c.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase) ? c : null;
        var maker = S("SystemManufacturer");
        var product = S("SystemProductName");
        return new SysBoard(S("BaseBoardManufacturer"), S("BaseBoardProduct"), S("BIOSVendor"), S("BIOSVersion"), S("BIOSReleaseDate"), SystemName(maker, product));
    }

    /// <summary>"Dell G15 5530", not "Dell Inc. Dell G15 5530": the maker only when the product does not name it.</summary>
    internal static string? SystemName(string? maker, string? product)
    {
        if (maker is null || product is null) return product ?? maker;
        var brand = maker.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)[0];
        return product.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? product : maker + " " + product;
    }

    // ---- graphics drivers (the display adapters' driver keys; the panel matches them with the GPUs it already shows)

    internal static List<SysGpu> ReadGpus()
    {
        var list = new List<SysGpu>();
        if (!OperatingSystem.IsWindows()) return list;
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (root is null) return list;
        foreach (var sub in root.GetSubKeyNames())
        {
            if (sub.Length != 4 || !sub.All(char.IsAsciiDigit)) continue;
            try
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string name || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)) continue;
                double? vram = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long q when q > 0 => q,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    _ => k.GetValue("HardwareInformation.MemorySize") switch { int i when i > 0 => (uint)i, byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0), _ => null },
                };
                list.Add(new SysGpu(name, k.GetValue("DriverVersion") as string, k.GetValue("DriverDate") as string, vram is > 0 ? Math.Round(vram.Value / 1073741824.0, 1) : null));
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list.GroupBy(g => g.Name).Select(g => g.First()).ToList();
    }

    // ---- displays (the current mode of every monitor on the desktop)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency, IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode dm);

    internal static List<SysDisplay> ReadDisplays()
    {
        var list = new List<SysDisplay>();
        if (!OperatingSystem.IsWindows()) return list;
        for (uint i = 0; i < 16; i++)
        {
            var dd = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & 0x1) == 0) continue; // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
            var dm = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettingsW(dd.DeviceName, -1, ref dm)) continue; // ENUM_CURRENT_SETTINGS
            list.Add(new SysDisplay(dm.PelsWidth, dm.PelsHeight, dm.DisplayFrequency, (dd.StateFlags & 0x4) != 0));
        }
        return list.OrderByDescending(d => d.Primary).ToList();
    }

    // ---- physical disks (IOCTLs that need no access rights, so no administrator)

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    private const uint IoctlStorageQueryProperty = 0x2D1400, IoctlDiskGetDriveGeometryEx = 0x700A0;

    internal static List<SysDisk> ReadDisks()
    {
        var list = new List<SysDisk>();
        if (!OperatingSystem.IsWindows()) return list;
        for (var n = 0; n < 16; n++)
        {
            using var h = CreateFileW($@"\\.\PhysicalDrive{n}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            var query = new byte[12]; // STORAGE_PROPERTY_QUERY: StorageDeviceProperty (0), PropertyStandardQuery (0)
            var desc = new byte[1024];
            if (!DeviceIoControl(h, IoctlStorageQueryProperty, query, query.Length, desc, desc.Length, out var got, IntPtr.Zero) || got < 32) continue;
            var model = AsciiAt(desc, BitConverter.ToInt32(desc, 16), got);
            var vendor = AsciiAt(desc, BitConverter.ToInt32(desc, 12), got);
            if (vendor is not null && model is not null && !model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) model = vendor + " " + model;
            var bus = Bus(BitConverter.ToInt32(desc, 28));
            double size = 0;
            var geo = new byte[256];
            if (DeviceIoControl(h, IoctlDiskGetDriveGeometryEx, [], 0, geo, geo.Length, out var g2, IntPtr.Zero) && g2 >= 32)
                size = BitConverter.ToInt64(geo, 24) / 1e9;
            bool? ssd = null;
            var seekQuery = new byte[12];
            BitConverter.GetBytes(7).CopyTo(seekQuery, 0); // StorageDeviceSeekPenaltyProperty
            var seek = new byte[12];
            if (DeviceIoControl(h, IoctlStorageQueryProperty, seekQuery, seekQuery.Length, seek, seek.Length, out var g3, IntPtr.Zero) && g3 >= 9)
                ssd = seek[8] == 0;
            if (bus == "NVMe") ssd = true;
            list.Add(new SysDisk(model ?? $"Disk {n}", Math.Round(size, 0), bus, ssd));
        }
        return list;
    }

    private static string? AsciiAt(byte[] buf, int offset, int length)
    {
        if (offset <= 0 || offset >= length) return null;
        var end = offset;
        while (end < length && buf[end] != 0) end++;
        var s = Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
        return s.Length > 0 ? string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)) : null;
    }

    internal static string? Bus(int busType) => busType switch
    {
        1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 17 => "NVMe", 19 => "UFS", _ => null,
    };

    // ---- volumes and network (read on every call)

    internal static List<SysVolume> Volumes() => DriveInfo.GetDrives()
        .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
        .Select(d => new SysVolume(d.Name.TrimEnd('\\', '/'), string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel, d.DriveFormat,
            Math.Round(d.TotalSize / 1e9, 1), Math.Round(d.AvailableFreeSpace / 1e9, 1)))
        .ToList();

    /// <summary>The adapters the network meters count (Settings › Ping &amp; network): local addresses only.</summary>
    internal static List<SysNet> Network(IReadOnlyList<string> chosen)
    {
        var list = new List<SysNet>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || !SystemMonitor.Counted(ni.Name, ni.Description, ni.NetworkInterfaceType, chosen)) continue;
            var ip = ni.GetIPProperties();
            var v4 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            var kind = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi" : "ethernet";
            list.Add(new SysNet(ni.Name, kind, ni.Speed > 0 ? ni.Speed / 1_000_000 : null,
                v4 is null ? null : $"{v4.Address}/{v4.PrefixLength}",
                ip.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString(),
                ip.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(2).Select(a => a.ToString()).ToList()));
        }
        return list;
    }
}

// ---------------------------------------------------------------- health check (pchealth)

/// <summary>
/// One check. Kind: disk (Name = the drive, Value = free %, A = free GB, B = total GB), ram (Value = load %, A = used GB,
/// B = total GB), uptime (Value = days), update (Windows Update waits for a restart: warn), recycle (A = GB, Value = items).
/// Level: ok, warn or bad.
/// </summary>
public sealed record HealthCheck(string Kind, string Level, string? Name, double? Value, double? A, double? B);
public sealed record HealthView(bool Enabled, IReadOnlyList<HealthCheck> Checks, bool CanClean);

public sealed class PcHealth(ConfigStore config, ILogger<PcHealth> log)
{
    public bool Wanted() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "pchealth");

    /// <summary>Emptying the Recycle Bin is opt-in per widget ("Let the phone empty the Recycle Bin", off by default).</summary>
    public bool CleanAllowed(string? deviceId, string? widget) =>
        PanelWidgets.Find(config.Get(), deviceId, widget, "pchealth")?.Bool("allowClean") == true;

    /// <summary>Overridable for tests: the checks' sources.</summary>
    internal Func<IReadOnlyList<HealthCheck>> Read { get; init; } = ReadChecks;
    internal Func<bool> Empty { get; init; } = EmptyRecycleBin;

    public HealthView View(string? deviceId, string? widget)
    {
        if (!Wanted()) return new HealthView(false, [], false);
        IReadOnlyList<HealthCheck> checks;
        try { checks = Read(); }
        catch (Exception ex) { log.LogDebug("pchealth: {Msg}", ex.Message); checks = []; }
        return new HealthView(true, checks, CleanAllowed(deviceId, widget));
    }

    /// <summary>Empties the Recycle Bin for the asking widget, if it allows it; null when done, else why not.</summary>
    public string? Clean(string? deviceId, string? widget)
    {
        if (!CleanAllowed(deviceId, widget)) return "Turn on \"Let the phone empty the Recycle Bin\" in this widget's settings";
        if (!OperatingSystem.IsWindows()) return "Only on Windows";
        if (!Empty()) return "The Recycle Bin could not be emptied";
        log.LogInformation("pchealth: Recycle Bin emptied from the phone");
        return null;
    }

    internal static string Level(double value, double warn, double bad, bool higherIsWorse = true) =>
        higherIsWorse ? value >= bad ? "bad" : value >= warn ? "warn" : "ok" : value <= bad ? "bad" : value <= warn ? "warn" : "ok";

    private static IReadOnlyList<HealthCheck> ReadChecks()
    {
        var list = new List<HealthCheck>();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.TotalSize > 0))
        {
            var freePct = d.AvailableFreeSpace * 100.0 / d.TotalSize;
            list.Add(new HealthCheck("disk", Level(freePct, 15, 5, higherIsWorse: false), d.Name.TrimEnd('\\', '/'), Math.Round(freePct, 1),
                Math.Round(d.AvailableFreeSpace / 1e9, 1), Math.Round(d.TotalSize / 1e9, 1)));
        }
        if (OperatingSystem.IsWindows())
        {
            var st = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() };
            if (GlobalMemoryStatusEx(ref st) && st.TotalPhys > 0)
            {
                var used = (st.TotalPhys - st.AvailPhys) / 1073741824.0;
                list.Add(new HealthCheck("ram", Level(st.MemoryLoad, 85, 95), null, st.MemoryLoad, Math.Round(used, 1), Math.Round(st.TotalPhys / 1073741824.0, 1)));
            }
        }
        var days = Environment.TickCount64 / 86_400_000.0;
        list.Add(new HealthCheck("uptime", Level(days, 7, 30), null, Math.Round(days, 4), null, null)); // to the minute, like the spec sheet
        if (OperatingSystem.IsWindows())
        {
            list.Add(new HealthCheck("update", RestartPending() ? "warn" : "ok", null, null, null, null));
            if (RecycleBin() is { } bin)
                list.Add(new HealthCheck("recycle", Level(bin.Bytes / 1e9, 10, 50), null, bin.Items, Math.Round(bin.Bytes / 1e9, 4), null));
        }
        return list;
    }

    /// <summary>Windows Update or a servicing operation waits for a restart (the keys Windows itself sets).</summary>
    internal static bool RestartPending()
    {
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var path in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
        })
        {
            try { using var k = Registry.LocalMachine.OpenSubKey(path); if (k is not null) return true; }
            catch (System.Security.SecurityException) { }
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemStatus status);

    // SHQUERYRBINFO: natural alignment on 64-bit Windows (24 bytes), the only builds RokeBox ships
    [StructLayout(LayoutKind.Sequential)] private struct RecycleInfo { public int Size; public long Bytes; public long Items; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBinW(string? root, ref RecycleInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBinW(IntPtr window, string? root, uint flags);

    private static (long Bytes, long Items)? RecycleBin()
    {
        var info = new RecycleInfo { Size = Marshal.SizeOf<RecycleInfo>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? (info.Bytes, info.Items) : null;
    }

    private static bool EmptyRecycleBin()
    {
        const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;
        var hr = SHEmptyRecycleBinW(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        return hr == 0 || hr == unchecked((int)0x8000FFFF); // E_UNEXPECTED: it was already empty
    }
}

// ---------------------------------------------------------------- data usage per day (datausage)

public sealed record UsageDay(string Date, long Down, long Up);
public sealed record DataUsageView(bool Enabled, string? Since, string Today, IReadOnlyList<UsageDay> Days);

/// <summary>
/// Adds up what the network meters' adapters receive and send, per local day, while a Data usage widget is on an enabled
/// page; kept in data-usage.json next to the agent's log (400 days). Counting starts when the widget is added.
/// </summary>
public sealed class DataUsageMonitor(ConfigStore config, ILogger<DataUsageMonitor> log) : BackgroundService
{
    internal const int KeepDays = 400;
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, (long Down, long Up)> _days = new(StringComparer.Ordinal);
    private Dictionary<string, (long Rx, long Tx)>? _prev;
    private string? _since;
    private bool _dirty, _loaded;

    internal string FilePath { get; init; } = Path.Combine(AppPaths.LocalDir, "data-usage.json");
    internal Func<DateTime> Now { get; init; } = () => DateTime.Now;
    internal Func<IReadOnlyList<string>, Dictionary<string, (long Rx, long Tx)>> Counters { get; init; } = ReadCounters;

    public bool Wanted() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "datausage");

    private string Today() => Now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public DataUsageView View()
    {
        if (!Wanted()) return new DataUsageView(false, null, Today(), []);
        lock (_gate)
        {
            LoadOnce();
            return new DataUsageView(true, _since, Today(), _days.Select(d => new UsageDay(d.Key, d.Value.Down, d.Value.Up)).ToList());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var savedAt = Environment.TickCount64;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!Wanted()) { lock (_gate) _prev = null; continue; } // not counting: the next start takes a new baseline
                try { Tick(); }
                catch (Exception ex) { log.LogDebug("datausage: {Msg}", ex.Message); }
                if (Environment.TickCount64 - savedAt > 60_000) { Save(); savedAt = Environment.TickCount64; }
            }
        }
        catch (OperationCanceledException) { }
        finally { Save(); }
    }

    /// <summary>One reading: what every counted adapter moved since the last one goes to today.</summary>
    internal void Tick()
    {
        var now = Counters(config.Get().Ping.Adapters);
        lock (_gate)
        {
            LoadOnce();
            if (_prev is not null)
            {
                long down = 0, up = 0;
                foreach (var (id, c) in now)
                {
                    if (!_prev.TryGetValue(id, out var p)) continue; // a new adapter counts from its next reading
                    // a counter that went back started over (the adapter was reset): what it counted since
                    down += c.Rx >= p.Rx ? c.Rx - p.Rx : c.Rx;
                    up += c.Tx >= p.Tx ? c.Tx - p.Tx : c.Tx;
                }
                if (down > 0 || up > 0)
                {
                    var key = Today();
                    var d = _days.GetValueOrDefault(key);
                    _days[key] = (d.Down + down, d.Up + up);
                    _since ??= key;
                    _dirty = true;
                    while (_days.Count > KeepDays) _days.Remove(_days.Keys.First());
                }
            }
            _prev = now;
        }
    }

    private void LoadOnce()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var root = JsonNode.Parse(File.ReadAllText(FilePath));
            _since = root?["since"]?.GetValue<string>();
            if (root?["days"] is JsonObject days)
                foreach (var (date, v) in days)
                    if (v is JsonArray { Count: 2 } a && DateOnly.TryParseExact(date, "yyyy-MM-dd", out _))
                        _days[date] = (a[0]!.GetValue<long>(), a[1]!.GetValue<long>());
        }
        catch (Exception ex) { log.LogWarning("datausage: {File} could not be read: {Msg}", FilePath, ex.Message); }
    }

    internal void Save()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            var days = new JsonObject();
            foreach (var (k, v) in _days) days[k] = new JsonArray(v.Down, v.Up);
            json = new JsonObject { ["since"] = _since, ["days"] = days }.ToJsonString();
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { log.LogWarning("datausage: {File} could not be saved: {Msg}", FilePath, ex.Message); }
    }

    private static Dictionary<string, (long Rx, long Tx)> ReadCounters(IReadOnlyList<string> chosen)
    {
        var counters = new Dictionary<string, (long Rx, long Tx)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || !SystemMonitor.Counted(ni.Name, ni.Description, ni.NetworkInterfaceType, chosen)) continue;
            try { var s = ni.GetIPStatistics(); counters[ni.Id] = (s.BytesReceived, s.BytesSent); }
            catch { }
        }
        return counters;
    }
}

// ---------------------------------------------------------------- sleep / shutdown / restart timer (powertimer)

public sealed record PowerTimerView(bool Enabled, bool Allowed, string? Action, long? EndsAt);

/// <summary>
/// Sleep, shut down or restart in N minutes (1 to 720). Shut down and restart go to Windows' own timer (shutdown.exe
/// /s|/r /t, which Windows announces and lets the user cancel; open programs are closed). Sleep is the agent's timer.
/// Allowed only with the Control Deck's power controls switched on in Settings, for a Power timer widget on an enabled page.
/// </summary>
public sealed class PowerTimer(ConfigStore config, ILogger<PowerTimer> log)
{
    internal static readonly string[] Actions = ["sleep", "shutdown", "restart"];
    private readonly Lock _gate = new();
    private string? _action;
    private long? _endsAt;
    private CancellationTokenSource? _sleep;

    internal Func<long> NowMs { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    /// <summary>Overridable for tests: runs shutdown.exe (tests record it).</summary>
    internal Action<string[]> Shutdown { get; init; } = RunShutdown;
    internal Action Suspend { get; init; } = () => { if (OperatingSystem.IsWindows()) SetSuspendState(false, false, false); };

    public bool Wanted() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "powertimer");

    public PowerTimerView View()
    {
        if (!Wanted()) return new PowerTimerView(false, false, null, null);
        lock (_gate)
        {
            if (_endsAt is { } end && end <= NowMs()) { _action = null; _endsAt = null; }
            return new PowerTimerView(true, config.Get().Controls.AllowPower, _action, _endsAt);
        }
    }

    /// <summary>Starts (or replaces) the timer for the asking widget; null when started, else a code (off, widget, invalid, unsupported).</summary>
    public string? Start(string? deviceId, string? widget, string? action, int minutes)
    {
        if (!config.Get().Controls.AllowPower) return "off";
        if (PanelWidgets.Find(config.Get(), deviceId, widget, "powertimer") is null) return "widget";
        if (action is null || !Actions.Contains(action) || minutes is < 1 or > 720) return "invalid";
        if (!OperatingSystem.IsWindows()) return "unsupported";
        var secs = minutes * 60;
        lock (_gate)
        {
            CancelCore();
            if (action == "sleep")
            {
                var cts = new CancellationTokenSource();
                _sleep = cts;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(secs), cts.Token); }
                    catch (OperationCanceledException) { return; }
                    lock (_gate)
                    {
                        if (_sleep != cts) return;
                        _sleep = null; _action = null; _endsAt = null;
                    }
                    log.LogInformation("power timer: sleeping now");
                    Suspend();
                });
            }
            else Shutdown([action == "restart" ? "/r" : "/s", "/t", secs.ToString(CultureInfo.InvariantCulture)]);
            _action = action;
            _endsAt = NowMs() + secs * 1000L;
        }
        log.LogInformation("power timer: {Action} in {Minutes} min", action, minutes);
        return null;
    }

    /// <summary>Cancels the timer for the asking widget (no power opt-in needed to call it off).</summary>
    public string? Cancel(string? deviceId, string? widget)
    {
        if (PanelWidgets.Find(config.Get(), deviceId, widget, "powertimer") is null) return "widget";
        lock (_gate) CancelCore();
        return null;
    }

    private void CancelCore()
    {
        if (_action is "shutdown" or "restart") Shutdown(["/a"]);
        _sleep?.Cancel();
        _sleep = null;
        _action = null;
        _endsAt = null;
    }

    private static void RunShutdown(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return;
        var psi = new ProcessStartInfo("shutdown.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is not null && !p.WaitForExit(10_000)) { try { p.Kill(); } catch { } }
    }

    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
}
