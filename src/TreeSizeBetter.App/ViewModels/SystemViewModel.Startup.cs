using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.SystemInfo;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>Eine Zeile im Autostart: Schalter an/aus, Rest zum Lesen.</summary>
public sealed class AutostartRow : ObservableObject
{
    private readonly Func<AutostartRow, bool, bool> _toggle;
    private bool _suppress;

    public AutostartRow(AutostartEntry entry, bool canChange, Func<AutostartRow, bool, bool> toggle)
    {
        Entry = entry;
        CanChange = canChange;
        _toggle = toggle;
        _isEnabled = entry.Enabled;
    }

    public AutostartEntry Entry { get; }
    public string Name => Entry.Name;
    public string Publisher => Entry.Publisher;
    public string Command => Entry.Command;
    public bool FileMissing => Entry.FileMissing;

    /// <summary>Fuer alle Benutzer und ohne Adminrechte: nur ansehen.</summary>
    public bool CanChange { get; }
    public bool IsLocked => !CanChange;

    public string LocationText => Entry.Location switch
    {
        AutostartLocation.BenutzerRegistry => Loc.T("startup.loc.userRegistry"),
        AutostartLocation.AlleRegistry => Loc.T("startup.loc.allRegistry"),
        AutostartLocation.AlleRegistry32 => Loc.T("startup.loc.allRegistry32"),
        AutostartLocation.BenutzerOrdner => Loc.T("startup.loc.userFolder"),
        AutostartLocation.StoreApp => Loc.T("startup.loc.store"),
        _ => Loc.T("startup.loc.allFolder")
    };

    public string StateText => FileMissing ? Loc.T("startup.state.missing")
        : IsEnabled ? Loc.T("startup.state.on") : Loc.T("startup.state.off");

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_suppress || value == _isEnabled) return;
            bool before = _isEnabled;
            SetProperty(ref _isEnabled, value);
            if (!_toggle(this, value))
            {
                // Hat nicht geklappt (z. B. fehlende Rechte): Schalter zurueck.
                _suppress = true;
                SetProperty(ref _isEnabled, before);
                _suppress = false;
            }
            OnPropertyChanged(nameof(StateText));
        }
    }
}

public sealed partial class SystemViewModel
{
    private AutostartManager _startup = null!;

    public ObservableCollection<AutostartRow> StartupRows { get; } = new();

    public IAsyncRelayCommand RefreshStartupCommand { get; private set; } = null!;

    private string _startupStatus = string.Empty;
    public string StartupStatus
    {
        get => _startupStatus;
        private set => SetProperty(ref _startupStatus, value);
    }

    /// <summary>Ohne Adminrechte lassen sich Eintraege fuer alle Benutzer nicht schalten.</summary>
    public bool HasLockedRows => StartupRows.Any(r => r.IsLocked);

    private void InitStartup()
    {
        _startup = AutostartManager.ForThisPc();
        RefreshStartupCommand = new AsyncRelayCommand(LoadStartupAsync);
    }

    private async Task LoadStartupAsync()
    {
        IReadOnlyList<AutostartEntry> entries = await Task.Run(_startup.ReadAll);
        bool elevated = Elevation.IsElevated;
        StartupRows.Clear();
        foreach (AutostartEntry entry in entries)
            StartupRows.Add(new AutostartRow(entry, (elevated || !entry.NeedsAdmin) && !entry.PolicyLocked, Toggle));
        UpdateStartupSummary();
        OnPropertyChanged(nameof(HasLockedRows));
    }

    private bool Toggle(AutostartRow row, bool enabled)
    {
        string? error = _startup.SetEnabled(row.Entry, enabled);
        if (error is null)
        {
            StartupStatus = Loc.F(enabled ? "startup.switchedOn" : "startup.switchedOff", row.Name);
            return true;
        }
        StartupStatus = Loc.F("startup.switchFailed", row.Name, error);
        return false;
    }

    private void UpdateStartupSummary()
    {
        int on = StartupRows.Count(r => r.IsEnabled && !r.FileMissing);
        StartupStatus = Loc.F("startup.summary", StartupRows.Count, on);
    }
}
