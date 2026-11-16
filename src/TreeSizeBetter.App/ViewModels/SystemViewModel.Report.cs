using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using TreeSizeBetter.Core.SystemInfo;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>
/// Systembericht fuer eine KI: vollstaendige Bestandsaufnahme aller Laufwerke,
/// Hardware und Autostart als eine Markdown-Datei. Die Auswertung macht die KI.
/// </summary>
public sealed partial class SystemViewModel
{
    private IAsyncRelayCommand? _reportCommand;
    public IAsyncRelayCommand ReportCommand => _reportCommand ??= new AsyncRelayCommand(CreateReportAsync, () => !IsReporting);

    private CancellationTokenSource? _reportCts;
    private IRelayCommand? _cancelReportCommand;
    public IRelayCommand CancelReportCommand => _cancelReportCommand ??= new RelayCommand(() => _reportCts?.Cancel());

    private bool _isReporting;
    public bool IsReporting
    {
        get => _isReporting;
        private set
        {
            if (!SetProperty(ref _isReporting, value)) return;
            ReportCommand.NotifyCanExecuteChanged();
        }
    }

    private string _reportStatus = string.Empty;
    public string ReportStatus
    {
        get => _reportStatus;
        private set => SetProperty(ref _reportStatus, value);
    }

    private async Task CreateReportAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("report.saveTitle"),
            Filter = "Markdown (*.md)|*.md",
            DefaultExt = ".md",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = AppPaths.AiReportDirectory,
            FileName = $"{Loc.T("report.fileName")}-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd}.md"
        };
        if (dialog.ShowDialog() != true) return;
        string path = dialog.FileName;

        IsReporting = true;
        _reportCts?.Dispose();
        _reportCts = new CancellationTokenSource();

        try
        {
            var status = new Progress<string>(text => ReportStatus = text);
            string markdown = await SystemReport.CreateAsync(_snapshot, status, _reportCts.Token);
            await File.WriteAllTextAsync(path, markdown, new UTF8Encoding(false));

            ActivityLog.Write("Bericht", $"Systembericht: {path}  ({SizeFormat.Bytes(new FileInfo(path).Length)})");
            ReportStatus = Loc.F("report.saved", path);
            MainViewModel.OpenInExplorer(path);
        }
        catch (OperationCanceledException)
        {
            ReportStatus = Loc.T("report.cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportStatus = Loc.F("report.failed", ex.Message);
        }
        finally
        {
            IsReporting = false;
        }
    }
}
