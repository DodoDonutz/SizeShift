using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TreeSizeBetter.Core.SystemInfo;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>
/// Eine Zeile der Prozess-Auswertung (ein Programm, alle Instanzen zusammen). Waehrend der
/// Aufzeichnung wird dieselbe Zeile nur mit neuen Werten versorgt statt neu angelegt -
/// das haelt die Tabelle ruhig und spart Rechenzeit.
/// </summary>
public sealed class ProcessRow : ObservableObject
{
    public ProcessRow(ProcessGroupStats stats) => Stats = stats;

    public ProcessGroupStats Stats { get; private set; }

    public static string KeyOf(ProcessGroupStats g) => g.Name + "\0" + g.Services;
    public string Key => KeyOf(Stats);

    public void Update(ProcessGroupStats stats)
    {
        Stats = stats;
        OnPropertyChanged(string.Empty);   // ein Signal fuer alle Spalten
    }

    public string Name => Stats.DisplayName;
    public string Description => string.IsNullOrWhiteSpace(Stats.Description) ? Stats.Publisher : Stats.Description;
    public string Publisher => Stats.Publisher;
    public string Parent => Stats.Parent;
    public int Instances => Stats.Instances;
    public string? Path => Stats.Path;
    public double CpuAvg => Stats.CpuAvgPercent;
    public double CpuPeak => Stats.CpuPeakPercent;
    public long RamAvg => Stats.RamAvgBytes;
    public long RamPeak => Stats.RamPeakBytes;
    public long Disk => Stats.ReadBytes + Stats.WriteBytes;
    public string CpuAvgText => Stats.CpuAvgPercent.ToString("0.00", Loc.Culture) + " %";
    public string CpuPeakText => Stats.CpuPeakPercent.ToString("0.0", Loc.Culture) + " %";
    public string RamAvgText => SizeFormat.Bytes(Stats.RamAvgBytes);
    public string RamPeakText => SizeFormat.Bytes(Stats.RamPeakBytes);
    public string DiskText => $"{SizeFormat.Bytes(Stats.ReadBytes)} / {SizeFormat.Bytes(Stats.WriteBytes)}";

    /// <summary>Balkenbreite fuer die CPU-Spalte (0..1, bezogen auf 25 % Dauerlast).</summary>
    public double CpuBar => Math.Min(1, Stats.CpuAvgPercent / 25);
}

/// <summary>Eine waehlbare Aufzeichnungsdauer.</summary>
public sealed record RecordDuration(TimeSpan Length, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Reiter "Prozesse": nimmt fuer eine gewaehlte Dauer auf, welche Programme laufen und
/// was sie verbrauchen, zeigt die Rangliste live und speichert am Ende einen Bericht.
/// </summary>
public sealed partial class SystemViewModel
{
    private CancellationTokenSource? _recordCts;
    private ProcessRecording? _recording;

    public ObservableCollection<ProcessRow> ProcessRows { get; } = new();
    private readonly Dictionary<string, ProcessRow> _rowsByKey = new();
    private IReadOnlyList<ProcessGroupStats> _shownGroups = Array.Empty<ProcessGroupStats>();

    private bool _showAllProcesses;
    /// <summary>Auch Programme zeigen, die kaum etwas verbraucht haben (sonst nur die auffaelligen).</summary>
    public bool ShowAllProcesses
    {
        get => _showAllProcesses;
        set
        {
            if (SetProperty(ref _showAllProcesses, value)) ShowGroups(_shownGroups);
        }
    }

    private string _processCountText = string.Empty;
    public string ProcessCountText
    {
        get => _processCountText;
        private set => SetProperty(ref _processCountText, value);
    }

    private bool _demoProcesses;

    /// <summary>Ohne Adminrechte fehlen bei vielen Diensten die Messwerte - darauf hinweisen.</summary>
    public bool ShowProcessAdminHint => !_demoProcesses && !TreeSizeBetter.Core.Scanning.Elevation.IsElevated;

    /// <summary>Nur fuer die README-Screenshots: eine erfundene, fertige Aufzeichnung anzeigen.</summary>
    public void ShowDemoProcesses(IReadOnlyList<ProcessGroupStats> groups, TimeSpan duration, double systemCpu)
    {
        _demoProcesses = true;
        OnPropertyChanged(nameof(ShowProcessAdminHint));
        ClearProcessRows();
        ShowGroups(groups);
        RecordProgress = 1;
        RecordStatus = Loc.F("procs.done", ProcessReport.FormatDuration(duration), groups.Count,
            systemCpu.ToString("0.0", Loc.Culture));
        OnPropertyChanged(nameof(ShowRecordIntro));
    }

    /// <summary>Gleicht die Tabelle mit den Messwerten ab: vorhandene Zeilen aktualisieren, neue einfuegen, alte entfernen.</summary>
    private void ShowGroups(IReadOnlyList<ProcessGroupStats> groups)
    {
        _shownGroups = groups;
        List<ProcessGroupStats> wanted = (ShowAllProcesses ? groups : groups.Where(ProcessReport.IsNotable)).ToList();
        var keys = new HashSet<string>(wanted.Select(ProcessRow.KeyOf));

        for (int i = ProcessRows.Count - 1; i >= 0; i--)
        {
            if (keys.Contains(ProcessRows[i].Key)) continue;
            _rowsByKey.Remove(ProcessRows[i].Key);
            ProcessRows.RemoveAt(i);
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            ProcessGroupStats g = wanted[i];
            string key = ProcessRow.KeyOf(g);
            if (_rowsByKey.TryGetValue(key, out ProcessRow? row))
            {
                row.Update(g);
                int at = ProcessRows.IndexOf(row);
                if (at != i) ProcessRows.Move(at, i);
            }
            else
            {
                row = new ProcessRow(g);
                _rowsByKey[key] = row;
                ProcessRows.Insert(i, row);
            }
        }

        ProcessCountText = groups.Count == 0 ? string.Empty : Loc.F("procs.count", wanted.Count, groups.Count);
    }

    private void ClearProcessRows()
    {
        ProcessRows.Clear();
        _rowsByKey.Clear();
        _shownGroups = Array.Empty<ProcessGroupStats>();
        ProcessCountText = string.Empty;
    }

    public IReadOnlyList<RecordDuration> RecordDurations { get; } = new[]
    {
        new RecordDuration(TimeSpan.FromMinutes(1), Loc.F("procs.duration.minutes", 1)),
        new RecordDuration(TimeSpan.FromMinutes(5), Loc.F("procs.duration.minutes", 5)),
        new RecordDuration(TimeSpan.FromMinutes(10), Loc.F("procs.duration.minutes", 10)),
        new RecordDuration(TimeSpan.FromMinutes(30), Loc.F("procs.duration.minutes", 30)),
        new RecordDuration(TimeSpan.FromHours(1), Loc.T("procs.duration.hour"))
    };

    private RecordDuration? _selectedDuration;
    public RecordDuration SelectedDuration
    {
        get => _selectedDuration ??= RecordDurations[1];
        set => SetProperty(ref _selectedDuration, value);
    }

    private IAsyncRelayCommand? _startRecordCommand;
    public IAsyncRelayCommand StartRecordCommand => _startRecordCommand ??= new AsyncRelayCommand(RecordAsync, () => !IsRecording);

    private IRelayCommand? _stopRecordCommand;
    public IRelayCommand StopRecordCommand => _stopRecordCommand ??= new RelayCommand(() => _recordCts?.Cancel(), () => IsRecording);

    private IAsyncRelayCommand? _saveProcessReportCommand;
    public IAsyncRelayCommand SaveProcessReportCommand =>
        _saveProcessReportCommand ??= new AsyncRelayCommand(SaveProcessReportAsync, () => _recording is not null && !IsRecording);

    private bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (!SetProperty(ref _isRecording, value)) return;
            StartRecordCommand.NotifyCanExecuteChanged();
            StopRecordCommand.NotifyCanExecuteChanged();
            SaveProcessReportCommand.NotifyCanExecuteChanged();
        }
    }

    private double _recordProgress;
    public double RecordProgress
    {
        get => _recordProgress;
        private set => SetProperty(ref _recordProgress, value);
    }

    private string _recordStatus = string.Empty;
    public string RecordStatus
    {
        get => _recordStatus;
        private set => SetProperty(ref _recordStatus, value);
    }

    public bool ShowRecordIntro => ProcessRows.Count == 0 && !IsRecording;

    private async Task RecordAsync()
    {
        TimeSpan duration = SelectedDuration.Length;
        // Kurze Aufzeichnungen feiner, lange sparsamer - die Last bleibt in jedem Fall gering.
        TimeSpan interval = duration <= TimeSpan.FromMinutes(10) ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5);

        _recordCts?.Dispose();
        _recordCts = new CancellationTokenSource();
        _recording = null;
        IsRecording = true;
        RecordProgress = 0;
        ClearProcessRows();
        RecordStatus = Loc.T("procs.starting");
        OnPropertyChanged(nameof(ShowRecordIntro));

        var progress = new Progress<(TimeSpan Elapsed, IReadOnlyList<ProcessGroupStats> Live)>(p =>
        {
            RecordProgress = Math.Min(1, p.Elapsed / duration);
            TimeSpan left = duration - p.Elapsed;
            RecordStatus = Loc.F("procs.running", ProcessReport.FormatDuration(p.Elapsed),
                ProcessReport.FormatDuration(left < TimeSpan.Zero ? TimeSpan.Zero : left));
            ShowGroups(p.Live);
        });

        try
        {
            _recording = await ProcessMonitor.RecordAsync(duration, interval, progress, _recordCts.Token);
            ShowGroups(_recording.Groups);
            RecordProgress = 1;
            RecordStatus = Loc.F("procs.done", ProcessReport.FormatDuration(_recording.Duration), _recording.Groups.Count,
                _recording.SystemCpuAvgPercent.ToString("0.0", Loc.Culture));
            ActivityLog.Write("Prozesse", $"Aufzeichnung {ProcessReport.FormatDuration(_recording.Duration)}, {_recording.Groups.Count} Programme");
        }
        catch (OperationCanceledException)
        {
            RecordStatus = Loc.T("procs.cancelled");
        }
        finally
        {
            IsRecording = false;
            OnPropertyChanged(nameof(ShowRecordIntro));
        }
    }

    private async Task SaveProcessReportAsync()
    {
        if (_recording is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("report.saveTitle"),
            Filter = "Markdown (*.md)|*.md",
            DefaultExt = ".md",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = AppPaths.AiReportDirectory,
            FileName = $"{Loc.T("procreport.fileName")}-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd-HHmm}.md"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            string markdown = ProcessReport.Build(_recording, _snapshot);
            await File.WriteAllTextAsync(dialog.FileName, markdown, new UTF8Encoding(false));
            ActivityLog.Write("Bericht", $"Prozess-Bericht: {dialog.FileName}");
            RecordStatus = Loc.F("report.saved", dialog.FileName);
            MainViewModel.OpenInExplorer(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordStatus = Loc.F("report.failed", ex.Message);
        }
    }
}
