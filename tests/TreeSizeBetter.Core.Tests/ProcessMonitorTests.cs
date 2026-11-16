using TreeSizeBetter.Core.SystemInfo;

namespace TreeSizeBetter.Core.Tests;

public class ProcessMonitorTests
{
    private const long Second = 10_000_000;   // 100-ns-Schritte

    private static ProcessSample P(int pid, string name, long cpuSeconds100ns, long ramMb, long readMb = 0, int parent = 1, long created = 100)
        => new(pid, parent, name, $@"C:\Apps\{name}", created, cpuSeconds100ns, ramMb << 20, ramMb << 20, readMb << 20, 0);

    [Fact]
    public void ComputesCpuShareOfAllCoresAndSumsInstances()
    {
        var agg = new ProcessAggregator(logicalCores: 4);
        // Explorer als Elternprozess, zwei Chrome-Prozesse und ein Spiel.
        agg.Add(new[] { P(1, "explorer.exe", 0, 100, parent: 0), P(10, "chrome.exe", 0, 300), P(11, "chrome.exe", 0, 200), P(20, "game.exe", 0, 2000, created: 5) }, 0);
        // Nach 1 s: Chrome 0,5 s + 0,5 s Rechenzeit, Spiel 2 s (= 2 von 4 Kernen).
        agg.Add(new[] { P(1, "explorer.exe", 0, 100, parent: 0), P(10, "chrome.exe", Second / 2, 300), P(11, "chrome.exe", Second / 2, 200), P(20, "game.exe", 2 * Second, 2000, readMb: 30, created: 5) }, Second);

        var (groups, systemAvg, _) = agg.Result();
        ProcessGroupStats chrome = Assert.Single(groups, g => g.Name == "chrome.exe");
        ProcessGroupStats game = Assert.Single(groups, g => g.Name == "game.exe");

        Assert.Equal(25, chrome.CpuAvgPercent, 3);           // 1 s von 4 Kern-Sekunden
        Assert.Equal(2, chrome.Instances);
        Assert.Equal(500L << 20, chrome.RamAvgBytes);         // beide Instanzen zusammen
        Assert.Equal("explorer.exe", chrome.Parent);
        Assert.Equal(50, game.CpuAvgPercent, 3);
        Assert.Equal(30L << 20, game.ReadBytes);
        Assert.Equal(75, systemAvg, 3);
        Assert.Equal("game.exe", groups[0].Name);             // nach CPU sortiert
    }

    [Fact]
    public void AReusedPidIsTreatedAsANewProcess()
    {
        var agg = new ProcessAggregator(1);
        agg.Add(new[] { P(7, "old.exe", 10 * Second, 10, created: 1) }, 0);
        // Gleiche PID, anderer Startzeitpunkt: neues Programm - keine Rechenzeit-Differenz zum alten.
        agg.Add(new[] { P(7, "new.exe", 20 * Second, 10, created: 2) }, Second);

        var (groups, _, _) = agg.Result();
        Assert.Equal(0, Assert.Single(groups, g => g.Name == "new.exe").CpuAvgPercent);
    }

    [Fact]
    public void ServiceHostsAreSplitByTheirServices()
    {
        var agg = new ProcessAggregator(1);
        ProcessSample Host(int pid, long cpu, string services) => P(pid, "svchost.exe", cpu, 20) with { Services = services };
        agg.Add(new[] { Host(30, 0, "Windows Update"), Host(31, 0, "DNS-Client"), P(32, "svchost.exe", 0, 5) }, 0);
        agg.Add(new[] { Host(30, Second / 2, "Windows Update"), Host(31, Second / 10, "DNS-Client"), P(32, "svchost.exe", 0, 5) }, Second);

        var (groups, _, _) = agg.Result();
        ProcessGroupStats update = Assert.Single(groups, g => g.Services == "Windows Update");
        Assert.Equal("svchost.exe", update.Name);
        Assert.Equal("svchost.exe (Windows Update)", update.DisplayName);
        Assert.Equal(50, update.CpuAvgPercent, 3);
        Assert.Equal(10, Assert.Single(groups, g => g.Services == "DNS-Client").CpuAvgPercent, 3);
        Assert.Equal("svchost.exe", Assert.Single(groups, g => g.Services is null).DisplayName);
    }

    [Theory]
    [InlineData("Clipboard User Service_4f2a1", "Clipboard User Service")]
    [InlineData("CDPUserSvc_a1b2c3d4", "CDPUserSvc")]
    [InlineData("Steam Client Service", "Steam Client Service")]
    [InlineData("My_Service_Name", "My_Service_Name")]
    public void UserServiceInstanceSuffixIsRemoved(string name, string expected) =>
        Assert.Equal(expected, ProcessMonitor.TrimInstanceSuffix(name));

    [Fact]
    public void ManyServicesInOneHostAreShortened()
    {
        Assert.Equal("A, B", ProcessMonitor.FormatServices(new[] { "B", "A", "a" }));
        Assert.Equal("A, B, C +2", ProcessMonitor.FormatServices(new[] { "E", "D", "C", "B", "A" }));
    }

    [Fact]
    public void RunningServicesAreFoundWithoutAdminRights()
    {
        Dictionary<int, string> services = ProcessMonitor.ServicesByProcess();
        Assert.NotEmpty(services);
        Assert.All(services, kv => Assert.True(kv.Key > 0 && kv.Value.Length > 0));
        // Mindestens ein svchost aus der Momentaufnahme sollte seine Dienste kennen.
        Assert.Contains(ProcessMonitor.TakeSnapshot(), s => s.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) && s.Services is not null);
    }

    [Fact]
    public void ReportListsNotableProgramsAndNamesTheRest()
    {
        var recording = new ProcessRecording
        {
            StartedLocal = new DateTime(2026, 11, 15, 10, 0, 0),
            Duration = TimeSpan.FromMinutes(5),
            Samples = 101,
            LogicalCores = 8,
            Elevated = true,
            SystemCpuAvgPercent = 12.5,
            SystemCpuPeakPercent = 60,
            Groups = new[]
            {
                new ProcessGroupStats { Name = "node.exe", CpuAvgPercent = 3.2, RamAvgBytes = 300L << 20, Instances = 4, Parent = "claude.exe", Path = @"C:\nodejs\node.exe" },
                new ProcessGroupStats { Name = "tiny|tool.exe", CpuAvgPercent = 0, RamAvgBytes = 2L << 20, Instances = 1 }
            }
        };

        string md = ProcessReport.Build(recording, null);
        Assert.Contains("node.exe", md);
        Assert.Contains("claude.exe", md);
        Assert.Contains(@"tiny\|tool.exe", md);   // nur in der Liste der ruhigen Programme, maskiert
        Assert.Equal(1, md.Split("node.exe").Length - 2);   // node.exe steht in der Tabelle, einmal im Pfad
        Assert.StartsWith("# ", md);
    }

    [Fact]
    public void ARealSnapshotFindsThisProcess()
    {
        IReadOnlyList<ProcessSample> snapshot = ProcessMonitor.TakeSnapshot();
        int me = Environment.ProcessId;
        ProcessSample self = Assert.Single(snapshot, s => s.Pid == me);
        Assert.True(self.WorkingSetBytes > 0);
        Assert.True(self.CpuTime100ns > 0);
    }
}
