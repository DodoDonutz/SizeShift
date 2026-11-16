using System.Windows;
using System.Windows.Media;
using TreeSizeBetter.App.Shell;

namespace TreeSizeBetter.App;

/// <summary>Kleine Eingabe, z. B. fuer den Namen eines neuen Ordners.</summary>
public partial class InputWindow : Window
{
    public InputWindow(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowChromeTheme.ApplyCurrent(this);
    }
}
