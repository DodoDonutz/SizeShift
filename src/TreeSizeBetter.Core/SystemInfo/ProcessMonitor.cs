using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TreeSizeBetter.Core.SystemInfo;

/// <summary>Ein Prozess zu einem Messzeitpunkt - Rohwerte, wie Windows sie liefert.</summary>
public readonly record struct ProcessSample(
    int Pid,
    int ParentPid,
    string Name,
    string? Path,
    long CreatedFileTime,
    long CpuTime100ns,
    long WorkingSetBytes,
    long PrivateBytes,
    long ReadBytes,
    long WriteBytes,
    string? Services = null);

/// <summary>Ergebnis einer Aufzeichnung je Programm (alle Instanzen zusammen, z. B. alle chrome.exe).</summary>
public sealed class ProcessGroupStats
{
    public required string Name { get; init; }

    /// <summary>Bei svchost.exe: die Windows-Dienste, die in diesem Prozess laufen (wie im Taskmanager).</summary>
    public string? Services { get; init; }

    /// <summary>Name fuer Anzeige und Bericht, bei Diensten mit deren Namen.</summary>
    public string DisplayName => string.IsNullOrEmpty(Services) ? Name : $"{Name} ({Services})";

    public string? Path { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;

    /// <summary>Wie viele verschiedene Prozesse (PID + Startzeit) waren es insgesamt.</summary>
    public int Instances { get; set; }

    /// <summary>Durchschnittliche CPU-Last in Prozent der gesamten Rechenleistung (alle Kerne = 100 %).</summary>
    public double CpuAvgPercent { get; set; }
    public double CpuPeakPercent { get; set; }

    public long RamAvgBytes { get; set; }
    public long RamPeakBytes { get; set; }

    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }

    /// <summary>Wer die Prozesse gestartet hat (haeufigster Elternprozess).</summary>
    public string Parent { get; set; } = string.Empty;

    /// <summary>Anteil der Messungen, in denen das Programm lief.</summary>
    public double PresentShare { get; set; }
}

public sealed class ProcessRecording
{
    public required DateTime StartedLocal { get; init; }
    public required TimeSpan Duration { get; init; }
    public required int Samples { get; init; }
    public required int LogicalCores { get; init; }
    public required bool Elevated { get; init; }
    public required double SystemCpuAvgPercent { get; init; }
    public required double SystemCpuPeakPercent { get; init; }
    public required IReadOnlyList<ProcessGroupStats> Groups { get; init; }
}

/// <summary>
/// Rechnet aus aufeinanderfolgenden Momentaufnahmen Durchschnitt und Spitzen je Programm.
/// CPU ergibt sich aus der Differenz der Rechenzeit zwischen zwei Messungen; Festplatte aus
/// der Differenz der gelesenen/geschriebenen Bytes. Prozesse werden ueber PID + Startzeit
/// erkannt, damit eine wiederverwendete PID nicht zwei Programme vermischt.
/// Ohne Windows-Aufrufe - deshalb mit Testdaten pruefbar.
/// </summary>
public sealed class ProcessAggregator
{
    private sealed class Group
    {
        public required string Name;
        public string? Services;
        public string? Path;
        public readonly HashSet<(int, long)> Instances = new();
        public double CpuSum, CpuPeak;
        public long RamSum, RamPeak, Read, Write;
        public int Present;
        public readonly Dictionary<string, int> Parents = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly int _cores;
    private readonly Dictionary<string, Group> _groups = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<(int, long), ProcessSample>? _previous;
    private long _previousTime;
    private int _intervals;
    private double _systemCpuSum, _systemCpuPeak;

    public ProcessAggregator(int logicalCores) => _cores = Math.Max(1, logicalCores);

    public int Intervals => _intervals;

    /// <summary>Nimmt eine Momentaufnahme auf. <paramref name="timeTicks"/> = Zeitstempel in 100-ns-Schritten.</summary>
    public void Add(IReadOnlyList<ProcessSample> snapshot, long timeTicks)
    {
        var current = new Dictionary<(int, long), ProcessSample>(snapshot.Count);
        foreach (ProcessSample s in snapshot) current[(s.Pid, s.CreatedFileTime)] = s;

        if (_previous is not null && timeTicks > _previousTime)
        {
            double elapsed = (timeTicks - _previousTime) * (double)_cores;
            var byKey = new Dictionary<string, (double Cpu, long Ram)>(StringComparer.OrdinalIgnoreCase);
            var names = current.ToDictionary(kv => kv.Value.Pid, kv => kv.Value.Name);
            double systemCpu = 0;

            foreach (var (key, s) in current)
            {
                string groupKey = KeyOf(s);
                Group g = GroupFor(groupKey, s);
                g.Path ??= s.Path;
                g.Instances.Add(key);
                if (names.TryGetValue(s.ParentPid, out string? parent))
                    g.Parents[parent] = g.Parents.GetValueOrDefault(parent) + 1;

                double cpu = 0;
                if (_previous.TryGetValue(key, out ProcessSample before))
                {
                    cpu = Math.Max(0, s.CpuTime100ns - before.CpuTime100ns) / elapsed * 100;
                    g.Read += Math.Max(0, s.ReadBytes - before.ReadBytes);
                    g.Write += Math.Max(0, s.WriteBytes - before.WriteBytes);
                }
                systemCpu += cpu;
                var sum = byKey.GetValueOrDefault(groupKey);
                byKey[groupKey] = (sum.Cpu + cpu, sum.Ram + s.WorkingSetBytes);
            }

            foreach (var (groupKey, (cpu, ram)) in byKey)
            {
                Group g = _groups[groupKey];
                g.CpuSum += cpu;
                g.CpuPeak = Math.Max(g.CpuPeak, cpu);
                g.RamSum += ram;
                g.RamPeak = Math.Max(g.RamPeak, ram);
                g.Present++;
            }

            _systemCpuSum += Math.Min(100, systemCpu);
            _systemCpuPeak = Math.Max(_systemCpuPeak, Math.Min(100, systemCpu));
            _intervals++;
        }

        _previous = current;
        _previousTime = timeTicks;
    }

    /// <summary>Programme nach Dateiname; svchost-Prozesse zusaetzlich nach ihren Diensten getrennt.</summary>
    private static string KeyOf(ProcessSample s) =>
        string.IsNullOrEmpty(s.Services) ? s.Name : s.Name + "\0" + s.Services;

    private Group GroupFor(string key, ProcessSample s)
    {
        if (!_groups.TryGetValue(key, out Group? g))
            _groups[key] = g = new Group { Name = s.Name, Services = s.Services };
        return g;
    }

    public (IReadOnlyList<ProcessGroupStats> Groups, double SystemAvg, double SystemPeak) Result()
    {
        int n = Math.Max(1, _intervals);
        var list = _groups
            .Where(kv => kv.Value.Present > 0)
            .Select(kv => new ProcessGroupStats
            {
                Name = kv.Value.Name,
                Services = kv.Value.Services,
                Path = kv.Value.Path,
                Instances = kv.Value.Instances.Count,
                CpuAvgPercent = kv.Value.CpuSum / n,
                CpuPeakPercent = kv.Value.CpuPeak,
                RamAvgBytes = kv.Value.RamSum / Math.Max(1, kv.Value.Present),
                RamPeakBytes = kv.Value.RamPeak,
                ReadBytes = kv.Value.Read,
                WriteBytes = kv.Value.Write,
                Parent = kv.Value.Parents.OrderByDescending(p => p.Value).Select(p => p.Key)
                    .FirstOrDefault(p => !string.Equals(p, kv.Value.Name, StringComparison.OrdinalIgnoreCase))
                    ?? kv.Value.Parents.Keys.FirstOrDefault() ?? string.Empty,
                PresentShare = (double)kv.Value.Present / n
            })
            .OrderByDescending(g => g.CpuAvgPercent)
            .ThenByDescending(g => g.RamAvgBytes)
            .ToList();
        return (list, _systemCpuSum / n, _systemCpuPeak);
    }
}

/// <summary>
/// Zeichnet ueber eine einstellbare Dauer auf, was laeuft und wie viel es verbraucht.
/// Nur lesend und sparsam: eine Momentaufnahme alle paar Sekunden ueber die Windows-
/// Prozessliste (Toolhelp) und je Prozess ein Handle mit minimalen Rechten - das kostet
/// weniger als ein Prozent einer Kern-Leistung. Ohne Administratorrechte fehlen bei
/// einigen Systemprozessen Pfad und Messwerte; die tauchen dann nur mit Namen auf.
/// </summary>
public static class ProcessMonitor
{
    public static async Task<ProcessRecording> RecordAsync(
        TimeSpan duration, TimeSpan interval, IProgress<(TimeSpan Elapsed, IReadOnlyList<ProcessGroupStats> Live)>? progress,
        CancellationToken ct)
    {
        var aggregator = new ProcessAggregator(Environment.ProcessorCount);
        DateTime started = DateTime.Now;
        var clock = Stopwatch.StartNew();
        int samples = 0;

        try
        {
            while (true)
            {
                IReadOnlyList<ProcessSample> snapshot = await Task.Run(TakeSnapshot, CancellationToken.None);
                aggregator.Add(snapshot, (long)(clock.Elapsed.TotalMilliseconds * 10_000));
                samples++;
                if (aggregator.Intervals > 0)
                    progress?.Report((clock.Elapsed, aggregator.Result().Groups));
                if (clock.Elapsed >= duration) break;
                TimeSpan wait = interval - TimeSpan.FromMilliseconds(clock.Elapsed.TotalMilliseconds % interval.TotalMilliseconds);
                await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException) when (aggregator.Intervals > 0)
        {
            // Vorzeitig beendet: was bis dahin gemessen wurde, gilt.
        }

        var (groups, systemAvg, systemPeak) = aggregator.Result();
        foreach (ProcessGroupStats g in groups) Describe(g);
        return new ProcessRecording
        {
            StartedLocal = started,
            Duration = clock.Elapsed,
            Samples = samples,
            LogicalCores = Environment.ProcessorCount,
            Elevated = Scanning.Elevation.IsElevated,
            SystemCpuAvgPercent = systemAvg,
            SystemCpuPeakPercent = systemPeak,
            Groups = groups
        };
    }

    /// <summary>Beschreibung und Hersteller aus der Programmdatei.</summary>
    private static void Describe(ProcessGroupStats g)
    {
        if (g.Path is null || !File.Exists(g.Path)) return;
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(g.Path);
            g.Description = info.FileDescription?.Trim() ?? string.Empty;
            g.Publisher = info.CompanyName?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    // ------------------------------------------------------------- Windows

    /// <summary>Alle Prozesse mit Elternprozess, Rechenzeit, Speicher und Ein-/Ausgabe.</summary>
    public static IReadOnlyList<ProcessSample> TakeSnapshot()
    {
        var result = new List<ProcessSample>(400);
        Dictionary<int, string>? services = null;
        IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == InvalidHandle) return result;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snap, ref entry)) return result;
            do
            {
                int pid = (int)entry.th32ProcessID;
                if (pid == 0) continue;   // Leerlaufprozess
                ProcessSample sample = Measure(pid, (int)entry.th32ParentProcessID, entry.szExeFile);
                if (string.Equals(entry.szExeFile, "svchost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    services ??= ServicesByProcess();
                    if (services.TryGetValue(pid, out string? names)) sample = sample with { Services = names };
                }
                result.Add(sample);
            }
            while (Process32NextW(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
        return result;
    }

    /// <summary>
    /// Welche Windows-Dienste in welchem Prozess laufen - ueber den Dienst-Manager, das geht
    /// auch ohne Administratorrechte. Mehrere Dienste in einem Prozess werden zusammengefasst.
    /// </summary>
    public static Dictionary<int, string> ServicesByProcess()
    {
        var byPid = new Dictionary<int, List<string>>();
        IntPtr scm = OpenSCManagerW(null, null, ScManagerEnumerateService);
        if (scm == IntPtr.Zero) return new Dictionary<int, string>();
        try
        {
            uint resume = 0;
            uint size = 64 * 1024;
            for (int round = 0; round < 8; round++)
            {
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    bool ok = EnumServicesStatusExW(scm, ScEnumProcessInfo, ServiceWin32, ServiceActive,
                        buffer, size, out uint needed, out uint count, ref resume, null);
                    if (!ok && Marshal.GetLastWin32Error() != ErrorMoreData) break;

                    int stride = Marshal.SizeOf<EnumServiceStatusProcess>();
                    for (int i = 0; i < count; i++)
                    {
                        var item = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + i * stride);
                        int pid = (int)item.ProcessId;
                        if (pid == 0) continue;
                        string name = Marshal.PtrToStringUni(item.DisplayName) ?? Marshal.PtrToStringUni(item.ServiceName) ?? "?";
                        if (!byPid.TryGetValue(pid, out List<string>? list)) byPid[pid] = list = new List<string>();
                        list.Add(TrimInstanceSuffix(name));
                    }

                    if (ok) break;
                    size = Math.Max(size, needed);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
        return byPid.ToDictionary(kv => kv.Key, kv => FormatServices(kv.Value));
    }

    /// <summary>"A, B, C" - bei vielen Diensten in einem Prozess nur die ersten drei und "+N".</summary>
    public static string FormatServices(IEnumerable<string> names)
    {
        List<string> sorted = names.Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        return sorted.Count <= 3 ? string.Join(", ", sorted) : string.Join(", ", sorted.Take(3)) + $" +{sorted.Count - 3}";
    }

    /// <summary>Benutzerdienste heissen z. B. "Clipboard User Service_4f2a1" - die Kennung je Anmeldung weg.</summary>
    public static string TrimInstanceSuffix(string name)
    {
        int underscore = name.LastIndexOf('_');
        if (underscore <= 0 || name.Length - underscore - 1 is < 4 or > 8) return name;
        for (int i = underscore + 1; i < name.Length; i++)
            if (!Uri.IsHexDigit(name[i])) return name;
        return name[..underscore];
    }

    private static ProcessSample Measure(int pid, int parent, string name)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return new ProcessSample(pid, parent, name, null, 0, 0, 0, 0, 0, 0);
        try
        {
            string? path = null;
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            if (QueryFullProcessImageNameW(h, 0, buffer, ref size)) path = buffer.ToString();

            long created = 0, cpu = 0;
            if (GetProcessTimes(h, out long c, out _, out long kernel, out long user))
            {
                created = c;
                cpu = kernel + user;
            }

            long ws = 0, priv = 0;
            var mem = new ProcessMemoryCounters { cb = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            if (K32GetProcessMemoryInfo(h, ref mem, mem.cb))
            {
                ws = (long)mem.WorkingSetSize;
                priv = (long)mem.PagefileUsage;
            }

            long read = 0, write = 0;
            if (GetProcessIoCounters(h, out IoCounters io))
            {
                read = (long)io.ReadTransferCount;
                write = (long)io.WriteTransferCount;
            }

            return new ProcessSample(pid, parent, name, path, created, cpu, ws, priv, read, write);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ScManagerEnumerateService = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceWin32 = 0x30;
    private const uint ServiceActive = 0x1;
    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint serviceType, uint serviceState,
        IntPtr services, uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
