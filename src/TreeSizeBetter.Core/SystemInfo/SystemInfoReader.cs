using System.Globalization;
using System.Management;
using System.Text;
using Microsoft.Win32;
using TreeSizeBetter.Core.Localization;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.SystemInfo;

public sealed record CpuInfo(string Name, int Cores, int Threads, int MaxClockMHz);

public sealed record MemoryModule(long CapacityBytes, int SpeedMHz, string Manufacturer, string PartNumber, string Slot);

public sealed record MemoryInfo(long TotalBytes, long FreeBytes, IReadOnlyList<MemoryModule> Modules)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

public sealed record GpuInfo(string Name, string DriverVersion, DateTime? DriverDate, long? MemoryBytes);

public sealed record OsInfo(string Caption, string DisplayVersion, string Build, DateTime? InstallDate, DateTime? LastBoot)
{
    public TimeSpan? Uptime => LastBoot is { } boot ? DateTime.Now - boot : null;
}

public enum DiskKind { Unbekannt, Hdd, Ssd, Nvme, Usb }

public enum DiskHealth { Unbekannt, Gesund, Warnung, Fehlerhaft }

public sealed record VolumeInfo(string Letter, string Label, string FileSystem, long SizeBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, SizeBytes - FreeBytes);
}

public sealed record DiskInfo(string Name, DiskKind Kind, DiskHealth Health, long SizeBytes, IReadOnlyList<VolumeInfo> Volumes);

public sealed record SystemSnapshot(
    string ComputerName,
    CpuInfo? Cpu,
    MemoryInfo? Memory,
    IReadOnlyList<GpuInfo> Gpus,
    OsInfo? Os,
    IReadOnlyList<DiskInfo> Disks);

/// <summary>
/// Liest Systeminformationen ueber dieselben Windows-Abfragen (WMI), die auch
/// Taskmanager und "Systeminformationen" nutzen. Nur lesend. Jeder Abschnitt
/// ist fuer sich abgesichert: fehlt eine Angabe, bleibt sie leer, statt das
/// Ganze scheitern zu lassen.
/// </summary>
public static class SystemInfoReader
{
    public static SystemSnapshot Read() => new(
        Environment.MachineName,
        Safe(ReadCpu),
        Safe(ReadMemory),
        Safe(ReadGpus) ?? Array.Empty<GpuInfo>(),
        Safe(ReadOs),
        Safe(ReadDisks) ?? Array.Empty<DiskInfo>());

    // ------------------------------------------------------------- Abschnitte

    private static CpuInfo? ReadCpu()
    {
        foreach (ManagementObject cpu in Query(@"root\cimv2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
        {
            return new CpuInfo(
                Str(cpu["Name"]).Trim(),
                ToInt(cpu["NumberOfCores"]),
                ToInt(cpu["NumberOfLogicalProcessors"]),
                ToInt(cpu["MaxClockSpeed"]));
        }
        return null;
    }

    private static MemoryInfo? ReadMemory()
    {
        long total = 0, free = 0;
        foreach (ManagementObject os in Query(@"root\cimv2", "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
        {
            total = ToLong(os["TotalVisibleMemorySize"]) * 1024;
            free = ToLong(os["FreePhysicalMemory"]) * 1024;
        }

        var modules = new List<MemoryModule>();
        foreach (ManagementObject m in Query(@"root\cimv2",
                     "SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory"))
        {
            int speed = ToInt(m["ConfiguredClockSpeed"]);
            if (speed == 0) speed = ToInt(m["Speed"]);
            modules.Add(new MemoryModule(ToLong(m["Capacity"]), speed, Str(m["Manufacturer"]).Trim(),
                Str(m["PartNumber"]).Trim(), Str(m["DeviceLocator"]).Trim()));
        }
        return total > 0 ? new MemoryInfo(total, free, modules) : null;
    }

    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        Dictionary<string, long> vram = ReadVideoMemoryFromRegistry();
        var result = new List<GpuInfo>();
        foreach (ManagementObject gpu in Query(@"root\cimv2", "SELECT Name, DriverVersion, DriverDate, AdapterRAM FROM Win32_VideoController"))
        {
            string name = Str(gpu["Name"]).Trim();
            if (name.Length == 0) continue;

            // AdapterRAM ist ein 32-Bit-Feld und endet bei 4 GB. Der Treiber traegt die
            // echte Groesse in der Registry ein - die gilt, wenn vorhanden.
            long? memory = vram.TryGetValue(name, out long fromRegistry) ? fromRegistry : NullIfZero(ToLong(gpu["AdapterRAM"]));
            result.Add(new GpuInfo(name, Str(gpu["DriverVersion"]), CimDate(gpu["DriverDate"]), memory));
        }
        return result;
    }

    private static Dictionary<string, long> ReadVideoMemoryFromRegistry()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using RegistryKey? cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return result;
            foreach (string sub in cls.GetSubKeyNames())
            {
                if (!sub.All(char.IsDigit)) continue;
                try
                {
                    using RegistryKey? key = cls.OpenSubKey(sub);
                    if (key?.GetValue("DriverDesc") is not string desc) continue;
                    object? size = key.GetValue("HardwareInformation.qwMemorySize") ?? key.GetValue("HardwareInformation.MemorySize");
                    long bytes = size switch
                    {
                        long l => l,
                        int i => (uint)i,
                        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        _ => 0
                    };
                    if (bytes > 0) result[desc.Trim()] = bytes;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return result;
    }

    private static OsInfo? ReadOs()
    {
        foreach (ManagementObject os in Query(@"root\cimv2", "SELECT Caption, BuildNumber, InstallDate, LastBootUpTime FROM Win32_OperatingSystem"))
        {
            string displayVersion = string.Empty;
            string build = Str(os["BuildNumber"]);
            try
            {
                using RegistryKey? cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                displayVersion = cv?.GetValue("DisplayVersion") as string ?? string.Empty;
                if (cv?.GetValue("UBR") is int ubr) build = $"{build}.{ubr}";
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
            return new OsInfo(Str(os["Caption"]).Replace("Microsoft ", string.Empty).Trim(), displayVersion, build,
                CimDate(os["InstallDate"]), CimDate(os["LastBootUpTime"]));
        }
        return null;
    }

    private static IReadOnlyList<DiskInfo> ReadDisks()
    {
        // Buchstaben je Datentraeger ueber die Partitionen zuordnen.
        var lettersByDisk = new Dictionary<string, List<string>>();
        foreach (ManagementObject part in Query(@"root\Microsoft\Windows\Storage", "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
        {
            char letter = part["DriveLetter"] is char c ? c : (char)ToInt(part["DriveLetter"]);
            if (!char.IsLetter(letter)) continue;
            string disk = Str(part["DiskNumber"]);
            if (!lettersByDisk.TryGetValue(disk, out var list)) lettersByDisk[disk] = list = new List<string>();
            list.Add(char.ToUpperInvariant(letter) + ":");
        }

        var volumes = new Dictionary<string, VolumeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                string letter = drive.Name.TrimEnd('\\');
                volumes[letter] = new VolumeInfo(letter, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.TotalFreeSpace);
            }
            catch (IOException)
            {
            }
        }

        var result = new List<DiskInfo>();
        foreach (ManagementObject disk in Query(@"root\Microsoft\Windows\Storage",
                     "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk"))
        {
            int media = ToInt(disk["MediaType"]);
            int bus = ToInt(disk["BusType"]);
            DiskKind kind = bus switch
            {
                17 => DiskKind.Nvme,
                7 => DiskKind.Usb,
                _ => media switch { 3 => DiskKind.Hdd, 4 => DiskKind.Ssd, _ => DiskKind.Unbekannt }
            };
            DiskHealth health = ToInt(disk["HealthStatus"]) switch
            {
                0 => DiskHealth.Gesund,
                1 => DiskHealth.Warnung,
                2 => DiskHealth.Fehlerhaft,
                _ => DiskHealth.Unbekannt
            };

            string id = Str(disk["DeviceId"]);
            List<VolumeInfo> vols = (lettersByDisk.TryGetValue(id, out var letters) ? letters : new List<string>())
                .Where(volumes.ContainsKey).Select(l => volumes[l]).OrderBy(v => v.Letter).ToList();
            result.Add(new DiskInfo(Str(disk["FriendlyName"]).Trim(), kind, health, ToLong(disk["Size"]), vols));
        }
        return result.OrderBy(d => d.Volumes.FirstOrDefault()?.Letter ?? "~").ToList();
    }

    // ------------------------------------------------------------- Text fuer die Zwischenablage

    /// <summary>Alles als schlichter Text, z. B. fuer eine Support-Anfrage oder ein Forum.</summary>
    public static string ToText(SystemSnapshot s)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Loc.F("system.text.header", s.ComputerName, DateTime.Now.ToString("g", Loc.Culture)));
        sb.AppendLine();

        if (s.Os is { } os)
        {
            sb.AppendLine(Loc.T("system.card.windows"));
            sb.AppendLine($"  {os.Caption} {os.DisplayVersion} (Build {os.Build})".TrimEnd());
            if (os.InstallDate is { } installed) sb.AppendLine("  " + Loc.F("system.installedOn", installed.ToString("d", Loc.Culture)));
            if (os.Uptime is { } up) sb.AppendLine("  " + Loc.F("system.uptime", FormatUptime(up)));
            sb.AppendLine();
        }
        if (s.Cpu is { } cpu)
        {
            sb.AppendLine(Loc.T("system.card.cpu"));
            sb.AppendLine($"  {cpu.Name}");
            sb.AppendLine("  " + Loc.F("system.cpu.cores", cpu.Cores, cpu.Threads, cpu.MaxClockMHz / 1000.0));
            sb.AppendLine();
        }
        if (s.Memory is { } mem)
        {
            sb.AppendLine(Loc.T("system.card.memory"));
            sb.AppendLine("  " + Loc.F("system.memory.usage", SizeFormat.Bytes(mem.UsedBytes), SizeFormat.Bytes(mem.TotalBytes)));
            foreach (MemoryModule m in mem.Modules)
                sb.AppendLine($"  {m.Slot}: {SizeFormat.Bytes(m.CapacityBytes)} {m.SpeedMHz} MT/s {m.Manufacturer} {m.PartNumber}".TrimEnd());
            sb.AppendLine();
        }
        foreach (GpuInfo gpu in s.Gpus)
        {
            sb.AppendLine(Loc.T("system.card.gpu"));
            sb.AppendLine($"  {gpu.Name}" + (gpu.MemoryBytes is { } vram ? $"  ({SizeFormat.Bytes(vram)})" : string.Empty));
            sb.AppendLine("  " + Loc.F("system.gpu.driver", gpu.DriverVersion,
                gpu.DriverDate?.ToString("d", Loc.Culture) ?? "–"));
            sb.AppendLine();
        }
        if (s.Disks.Count > 0)
        {
            sb.AppendLine(Loc.T("system.card.disks"));
            foreach (DiskInfo d in s.Disks)
            {
                sb.AppendLine($"  {d.Name}  ·  {KindText(d.Kind)}  ·  {SizeFormat.Bytes(d.SizeBytes)}  ·  {HealthText(d.Health)}");
                foreach (VolumeInfo v in d.Volumes)
                    sb.AppendLine("    " + Loc.F("system.volume.line", v.Letter, v.Label, SizeFormat.Bytes(v.UsedBytes),
                        SizeFormat.Bytes(v.SizeBytes), SizeFormat.Bytes(v.FreeBytes)).Replace("  ", " "));
            }
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string KindText(DiskKind kind) => kind switch
    {
        DiskKind.Hdd => "HDD",
        DiskKind.Ssd => "SSD",
        DiskKind.Nvme => "NVMe-SSD",
        DiskKind.Usb => "USB",
        _ => Loc.T("system.disk.unknownType")
    };

    public static string HealthText(DiskHealth health) => health switch
    {
        DiskHealth.Gesund => Loc.T("system.health.ok"),
        DiskHealth.Warnung => Loc.T("system.health.warning"),
        DiskHealth.Fehlerhaft => Loc.T("system.health.bad"),
        _ => Loc.T("system.health.unknown")
    };

    public static string FormatUptime(TimeSpan up) =>
        up.TotalDays >= 1
            ? Loc.F("system.uptime.days", (int)up.TotalDays, up.Hours, up.Minutes)
            : Loc.F("system.uptime.hours", up.Hours, up.Minutes);

    // ------------------------------------------------------------- Hilfen

    private static IEnumerable<ManagementObject> Query(string scope, string wql)
    {
        var results = new List<ManagementObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            foreach (ManagementBaseObject obj in searcher.Get())
                if (obj is ManagementObject mo) results.Add(mo);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }
        return results;
    }

    private static T? Safe<T>(Func<T> read) where T : class
    {
        try { return read(); }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException or InvalidCastException or FormatException)
        {
            return null;
        }
    }

    private static string Str(object? value) => value?.ToString() ?? string.Empty;

    private static int ToInt(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return 0; }
    }

    private static long ToLong(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return 0; }
    }

    private static long? NullIfZero(long value) => value > 0 ? value : null;

    private static DateTime? CimDate(object? value)
    {
        if (value is not string text || text.Length < 14) return null;
        try { return ManagementDateTimeConverter.ToDateTime(text); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or ArgumentOutOfRangeException) { return null; }
    }
}
