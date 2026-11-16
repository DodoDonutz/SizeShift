namespace TreeSizeBetter.Core.Util;

/// <summary>
/// Einfache Platzhalter fuer Dateinamen: "*" steht fuer beliebig viele Zeichen
/// innerhalb eines Pfadsegments, Gross-/Kleinschreibung egal. Bewusst ohne
/// Regex - die Muster stehen im Regelkatalog und sollen lesbar bleiben.
/// </summary>
public static class Wildcard
{
    public static bool IsMatch(string text, string pattern)
    {
        string[] textParts = text.Split('\\');
        string[] patternParts = pattern.Split('\\');
        if (textParts.Length != patternParts.Length) return false;
        for (int i = 0; i < textParts.Length; i++)
            if (!SegmentMatch(textParts[i], patternParts[i])) return false;
        return true;
    }

    private static bool SegmentMatch(string text, string pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t]))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
