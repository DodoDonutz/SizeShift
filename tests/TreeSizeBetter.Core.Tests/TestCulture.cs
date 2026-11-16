using System.Runtime.CompilerServices;
using TreeSizeBetter.Core.Localization;

// Ein Test schaltet die Sprache um; parallel liefen andere Tests dann in der falschen Sprache.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TreeSizeBetter.Core.Tests;

/// <summary>
/// Die Tests pruefen an einigen Stellen deutsche Meldungstexte und Zahlenformate.
/// Damit sie auf jedem Rechner gleich laufen, gilt fuer alle Tests Deutsch -
/// unabhaengig von der Windows-Sprache.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseGerman() => Loc.SetLanguage("de");
}
