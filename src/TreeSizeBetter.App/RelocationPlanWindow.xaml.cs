using System.Windows;
using System.Windows.Media;
using TreeSizeBetter.App.Shell;
using TreeSizeBetter.App.ViewModels;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.App;

/// <summary>
/// Uebersicht vor dem Umlagern: jeder Ordner mit Ziel, Groesse und Pruefergebnis.
/// Gesperrte Ordner laufen nicht mit; Ordner mit Warnung nur, wenn der Haken
/// gesetzt ist.
/// </summary>
public partial class RelocationPlanWindow : Window
{
    private readonly IReadOnlyList<PlanItem> _plan;

    public RelocationPlanWindow(IReadOnlyList<PlanItem> plan, string targetFolder)
    {
        InitializeComponent();
        _plan = plan;
        Items.ItemsSource = plan;

        int blocked = plan.Count(p => p.IsBlocked);
        HeadlineText.Text = plan.Count == 1 ? Loc.F("plan.headlineOne", plan[0].Name) : Loc.F("plan.headlineMany", plan.Count);
        SubText.Text = Loc.F("plan.sub", targetFolder) +
                       blocked switch
                       {
                           0 => string.Empty,
                           1 when plan.Count == 1 => " " + Loc.T("plan.blockedSelf"),
                           1 => " " + Loc.T("plan.blockedOne"),
                           _ => " " + Loc.F("plan.blockedMany", blocked)
                       };
        AcceptWarnings.Visibility = plan.Any(p => p.HasWarnings && !p.IsBlocked) ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton();
    }

    public PlanDecision? Decision { get; private set; }

    private List<PlanItem> Approved => _plan
        .Where(p => !p.IsBlocked && (!p.HasWarnings || AcceptWarnings.IsChecked == true))
        .ToList();

    private void UpdateButton()
    {
        List<PlanItem> approved = Approved;
        OkButton.IsEnabled = approved.Count > 0;
        OkButton.Content = approved.Count == 0
            ? Loc.T("plan.nothing")
            : Loc.F("plan.ok", approved.Count, SizeFormat.Bytes(approved.Sum(p => p.Bytes)));
    }

    private void Accept_Changed(object sender, RoutedEventArgs e) => UpdateButton();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Decision = new PlanDecision(Approved, AcceptWarnings.IsChecked == true);
        DialogResult = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowChromeTheme.ApplyCurrent(this);
    }
}
