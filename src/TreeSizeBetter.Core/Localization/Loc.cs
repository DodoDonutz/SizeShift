using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace TreeSizeBetter.Core.Localization;

/// <summary>
/// Texte der Oberflaeche in der gewaehlten Sprache. Je Sprache liegt eine
/// eingebettete JSON-Datei mit flachen Schluesseln in diesem Ordner
/// (de.json, en.json, ...). Eine neue Sprache braucht nur eine weitere Datei
/// und einen Eintrag in <see cref="Supported"/>.
///
/// Fehlt ein Schluessel in einer Sprache, gilt Englisch, dann der Schluessel selbst -
/// so faellt ein Loch auf, statt die Oberflaeche zu sprengen.
/// </summary>
public static class Loc
{
    /// <summary>Unterstuetzte Sprachen als Zwei-Buchstaben-Code, in der Reihenfolge fuers Menue.</summary>
    public static readonly IReadOnlyList<string> Supported = new[] { "de", "en", "fr", "es" };

    /// <summary>Gilt, wenn die Windows-Sprache keine der unterstuetzten ist.</summary>
    public const string Fallback = "en";

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _strings;
    private static Dictionary<string, string>? _fallback;
    private static CultureInfo? _culture;

    /// <summary>Aktive Sprache, z. B. "de".</summary>
    public static string Language
    {
        get
        {
            EnsureLoaded();
            return _language!;
        }
    }

    private static string? _language;

    /// <summary>Kultur fuer Zahlen und Datumsangaben, passend zur Sprache.</summary>
    public static CultureInfo Culture
    {
        get
        {
            EnsureLoaded();
            return _culture!;
        }
    }

    /// <summary>
    /// Sprache, die Windows anzeigt, wenn sie unterstuetzt wird - sonst Englisch.
    /// </summary>
    public static string SystemLanguage()
    {
        string code = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
        try
        {
            // CurrentUICulture ist die Anzeigesprache des Benutzers; vor einem
            // SetLanguage hat sie noch niemand ueberschrieben.
            code = _originalUiCulture.TwoLetterISOLanguageName;
        }
        catch
        {
            // InstalledUICulture bleibt.
        }
        return Supported.Contains(code) ? code : Fallback;
    }

    private static readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;
    private static readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;

    /// <summary>
    /// Schaltet die Sprache um. null oder leer: wie Windows. Setzt zugleich die
    /// Kultur des Prozesses, damit Zahlen und Datumsangaben passen.
    /// </summary>
    public static void SetLanguage(string? code)
    {
        lock (Gate)
        {
            string language = string.IsNullOrWhiteSpace(code) || !Supported.Contains(code.ToLowerInvariant())
                ? SystemLanguage()
                : code.ToLowerInvariant();

            _strings = Load(language);
            _fallback = language == Fallback ? _strings : Load(Fallback);
            _language = language;
            _culture = CultureFor(language);

            CultureInfo.DefaultThreadCurrentCulture = _culture;
            CultureInfo.DefaultThreadCurrentUICulture = _culture;
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _culture;
        }
    }

    /// <summary>Text zum Schluessel.</summary>
    public static string T(string key)
    {
        EnsureLoaded();
        if (_strings!.TryGetValue(key, out string? text)) return text;
        if (_fallback!.TryGetValue(key, out text)) return text;
        return key;
    }

    /// <summary>Text zum Schluessel mit Platzhaltern {0}, {1} ... in der aktiven Kultur.</summary>
    public static string F(string key, params object?[] args)
        => string.Format(Culture, T(key), args);

    /// <summary>Alle Schluessel einer Sprache - fuer den Vollstaendigkeitstest.</summary>
    public static IReadOnlyDictionary<string, string> Table(string language) => Load(language);

    private static void EnsureLoaded()
    {
        if (_strings is null) SetLanguage(null);
    }

    /// <summary>
    /// Die Benutzerkultur behalten, wenn sie zur Sprache passt (de-AT bleibt de-AT),
    /// sonst die uebliche Kultur der Sprache.
    /// </summary>
    private static CultureInfo CultureFor(string language)
    {
        if (string.Equals(_originalCulture.TwoLetterISOLanguageName, language, StringComparison.OrdinalIgnoreCase))
            return _originalCulture;
        return language switch
        {
            "de" => CultureInfo.GetCultureInfo("de-DE"),
            "fr" => CultureInfo.GetCultureInfo("fr-FR"),
            "es" => CultureInfo.GetCultureInfo("es-ES"),
            _ => CultureInfo.GetCultureInfo("en-US")
        };
    }

    private static Dictionary<string, string> Load(string language)
    {
        Assembly assembly = typeof(Loc).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream($"TreeSizeBetter.Core.Localization.{language}.json");
        if (stream is null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream,
                   new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
               ?? new Dictionary<string, string>();
    }
}
