using System.Windows.Markup;

namespace TreeSizeBetter.App.Shell;

/// <summary>
/// Text in XAML aus der Sprachtabelle: <c>Content="{loc:T analysis.scan}"</c>.
/// Wird beim Aufbau des Fensters einmal aufgeloest; ein Sprachwechsel greift
/// deshalb nach einem Neustart.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
