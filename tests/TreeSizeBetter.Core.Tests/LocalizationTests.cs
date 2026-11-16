using System.Text.RegularExpressions;
using TreeSizeBetter.Core.Localization;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Tests;

public class LocalizationTests
{
    [Fact]
    public void AllLanguagesHaveTheSameKeys()
    {
        var reference = Loc.Table("de");
        Assert.NotEmpty(reference);
        foreach (string language in Loc.Supported)
        {
            var table = Loc.Table(language);
            Assert.Empty(reference.Keys.Except(table.Keys));
            Assert.Empty(table.Keys.Except(reference.Keys));
        }
    }

    [Fact]
    public void PlaceholdersMatchAcrossLanguages()
    {
        var reference = Loc.Table("de");
        foreach (string language in Loc.Supported)
        {
            var table = Loc.Table(language);
            foreach (var (key, german) in reference)
                Assert.True(Placeholders(german).SetEquals(Placeholders(table[key])), $"{language}: {key}");
        }
    }

    [Fact]
    public void EveryTextFormatsWithoutError()
    {
        object[] args = Enumerable.Range(0, 10).Select(i => (object)i).ToArray();
        foreach (string language in Loc.Supported)
            foreach (var (key, text) in Loc.Table(language))
                _ = string.Format(text, args);    // wirft bei kaputten Klammern
    }

    [Fact]
    public void SwitchingLanguageChangesTexts()
    {
        try
        {
            Loc.SetLanguage("en");
            Assert.Equal("yesterday", SizeFormat.Ago(1.5));
            Loc.SetLanguage("fr");
            Assert.Equal("il y a 5 jours", SizeFormat.Ago(5));
            Loc.SetLanguage("es");
            Assert.Equal("hace 5 días", SizeFormat.Ago(5));
            Loc.SetLanguage("xx");      // unbekannt: wie Windows, sonst Englisch
            Assert.Contains(Loc.Language, Loc.Supported);
        }
        finally
        {
            Loc.SetLanguage("de");
        }
    }

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)").Select(m => m.Groups[1].Value).ToHashSet();
}
