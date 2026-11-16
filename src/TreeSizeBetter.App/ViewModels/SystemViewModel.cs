using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TreeSizeBetter.Core.SystemInfo;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>Ein Laufwerk (Partition) auf einer Datentraeger-Karte.</summary>
public sealed record VolumeRow(string Text, double Share);

/// <summary>Ein physischer Datentraeger mit Typ, Zustand und seinen Laufwerksbuchstaben.</summary>
public sealed record DiskRow(string Title, string Detail, string HealthText, DiskHealth Health, IReadOnlyList<VolumeRow> Volumes)
{
    public bool IsHealthy => Health == DiskHealth.Gesund;
    public bool IsWarning => Health == DiskHealth.Warnung;
    public bool IsBad => Health == DiskHealth.Fehlerhaft;
}

public sealed record GpuRow(string Name, string Detail);

/// <summary>
/// Der Bereich "System": Uebersicht ueber Hardware und Windows, dazu der
/// Autostart. Die Uebersicht wird beim ersten Oeffnen gelesen - nur lesend.
/// </summary>
public sealed partial class SystemViewModel : ObservableObject
{
    public SystemViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        CopyCommand = new RelayCommand(Copy, () => _snapshot is not null);
        InitStartup();
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand CopyCommand { get; }

    private SystemSnapshot? _snapshot;
    private bool _loadedOnce;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value)) return;
            RefreshCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasData));
        }
    }

    public bool HasData => _snapshot is not null && !IsLoading;

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // ------------------------------------------------------------- Karten

    public string ComputerName => _snapshot?.ComputerName ?? string.Empty;

    public string OsTitle { get; private set; } = string.Empty;
    public string OsDetail { get; private set; } = string.Empty;
    public string UptimeText { get; private set; } = string.Empty;

    public string CpuName { get; private set; } = string.Empty;
    public string CpuDetail { get; private set; } = string.Empty;

    public string MemoryText { get; private set; } = string.Empty;
    public double MemoryShare { get; private set; }
    public ObservableCollection<string> MemoryModules { get; } = new();

    public ObservableCollection<GpuRow> Gpus { get; } = new();
    public ObservableCollection<DiskRow> Disks { get; } = new();

    /// <summary>Demo-Modus fuer Screenshots: erfundene Hardware und ein erfundener Autostart statt der echten.</summary>
    public void ShowDemo(SystemSnapshot snapshot, IReadOnlyList<TreeSizeBetter.Core.SystemInfo.AutostartEntry> autostart)
    {
        _loadedOnce = true;
        _snapshot = snapshot;
        Fill(snapshot);
        StatusText = Loc.T("system.readOnly");
        CopyCommand.NotifyCanExecuteChanged();
        StartupRows.Clear();
        foreach (var entry in autostart) StartupRows.Add(new AutostartRow(entry, !entry.NeedsAdmin, (_, _) => true));
        UpdateStartupSummary();
        OnPropertyChanged(nameof(HasLockedRows));
    }

    /// <summary>Beim ersten Oeffnen des Bereichs.</summary>
    public void EnsureLoaded()
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        _ = LoadAsync();
        _ = LoadStartupAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        StatusText = Loc.T("system.loading");
        try
        {
            _snapshot = await Task.Run(SystemInfoReader.Read);
            Fill(_snapshot);
            StatusText = Loc.T("system.readOnly");
        }
        catch (Exception ex)
        {
            StatusText = Loc.F("system.failed", ex.Message);
        }
        finally
        {
            IsLoading = false;
            CopyCommand.NotifyCanExecuteChanged();
        }
    }

    private void Fill(SystemSnapshot s)
    {
        if (s.Os is { } os)
        {
            OsTitle = $"{os.Caption} {os.DisplayVersion}".Trim();
            OsDetail = "Build " + os.Build +
                       (os.InstallDate is { } d ? "  ·  " + Loc.F("system.installedOn", d.ToString("d", Loc.Culture)) : string.Empty);
            UptimeText = os.Uptime is { } up ? Loc.F("system.uptime", SystemInfoReader.FormatUptime(up)) : string.Empty;
        }

        if (s.Cpu is { } cpu)
        {
            CpuName = cpu.Name;
            CpuDetail = Loc.F("system.cpu.cores", cpu.Cores, cpu.Threads, cpu.MaxClockMHz / 1000.0);
        }

        MemoryModules.Clear();
        if (s.Memory is { } mem)
        {
            MemoryText = Loc.F("system.memory.usage", SizeFormat.Bytes(mem.UsedBytes), SizeFormat.Bytes(mem.TotalBytes));
            MemoryShare = mem.TotalBytes > 0 ? (double)mem.UsedBytes / mem.TotalBytes : 0;
            foreach (MemoryModule m in mem.Modules)
                MemoryModules.Add(string.Join("  ·  ", new[]
                {
                    m.Slot, SizeFormat.Bytes(m.CapacityBytes), m.SpeedMHz > 0 ? $"{m.SpeedMHz} MT/s" : string.Empty,
                    m.Manufacturer, m.PartNumber
                }.Where(x => !string.IsNullOrWhiteSpace(x))));
        }

        Gpus.Clear();
        foreach (GpuInfo gpu in s.Gpus)
        {
            string detail = Loc.F("system.gpu.driver", gpu.DriverVersion, gpu.DriverDate?.ToString("d", Loc.Culture) ?? "–");
            if (gpu.MemoryBytes is { } vram) detail = Loc.F("system.gpu.memory", SizeFormat.Bytes(vram)) + "  ·  " + detail;
            Gpus.Add(new GpuRow(gpu.Name, detail));
        }

        Disks.Clear();
        foreach (DiskInfo d in s.Disks)
        {
            var volumes = d.Volumes.Select(v => new VolumeRow(
                Loc.F("system.volume.line", v.Letter, v.Label, SizeFormat.Bytes(v.UsedBytes), SizeFormat.Bytes(v.SizeBytes), SizeFormat.Bytes(v.FreeBytes)).Replace("  ", " "),
                v.SizeBytes > 0 ? (double)v.UsedBytes / v.SizeBytes : 0)).ToList();
            Disks.Add(new DiskRow(d.Name, $"{SystemInfoReader.KindText(d.Kind)}  ·  {SizeFormat.Bytes(d.SizeBytes)}",
                SystemInfoReader.HealthText(d.Health), d.Health, volumes));
        }

        foreach (string name in new[]
                 {
                     nameof(ComputerName), nameof(OsTitle), nameof(OsDetail), nameof(UptimeText), nameof(CpuName),
                     nameof(CpuDetail), nameof(MemoryText), nameof(MemoryShare), nameof(HasData)
                 })
            OnPropertyChanged(name);
    }

    private void Copy()
    {
        if (_snapshot is null) return;
        try
        {
            Clipboard.SetText(SystemInfoReader.ToText(_snapshot));
            StatusText = Loc.T("system.copied");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Zwischenablage gerade von einem anderen Programm belegt.
            StatusText = Loc.T("system.copyFailed");
        }
    }
}
