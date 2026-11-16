using System.Text;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Analysis;

public enum EntryKind
{
    Everything,
    DirectoriesOnly,
    FilesOnly
}

/// <summary>
/// Filter fuer Tabelle und Export. Nach dem Erzeugen unveraenderlich, damit er
/// gefahrlos im Hintergrund-Thread verwendet werden kann, waehrend die UI
/// weiterlaeuft.
/// </summary>
public sealed class EntryFilter
{
    private readonly byte[]? _nameNeedle;
    private readonly byte[][] _extensions;

    public EntryFilter(
        long minSize = 0,
        double? minColdDays = null,
        double? maxColdDays = null,
        EntryKind kind = EntryKind.Everything,
        string? nameContains = null,
        string? extensions = null,
        bool excludeLinks = false)
    {
        MinSize = minSize;
        MinColdDays = minColdDays;
        MaxColdDays = maxColdDays;
        Kind = kind;
        NameContains = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains.Trim();
        ExcludeLinks = excludeLinks;

        _nameNeedle = NameContains is null
            ? null
            : Encoding.UTF8.GetBytes(NameContains.ToLowerInvariant());

        _extensions = ParseExtensions(extensions);
        ExtensionText = extensions ?? string.Empty;
    }

    public long MinSize { get; }
    public double? MinColdDays { get; }
    public double? MaxColdDays { get; }
    public EntryKind Kind { get; }
    public string? NameContains { get; }
    public string ExtensionText { get; }
    public bool ExcludeLinks { get; }

    public bool IsEmpty =>
        MinSize == 0 && MinColdDays is null && MaxColdDays is null &&
        Kind == EntryKind.Everything && _nameNeedle is null &&
        _extensions.Length == 0 && !ExcludeLinks;

    public bool Accepts(ScanTree tree, int index, long now, bool trustAccessTimes)
    {
        ref FsEntry e = ref tree.Entries[index];

        switch (Kind)
        {
            case EntryKind.DirectoriesOnly when !e.IsDirectory: return false;
            case EntryKind.FilesOnly when e.IsDirectory: return false;
        }

        if (ExcludeLinks && e.IsReparsePoint) return false;

        if (MinSize > 0 && tree.SizeOf(index) < MinSize) return false;

        if (MinColdDays is not null || MaxColdDays is not null)
        {
            double? cold = AgeAnalyzer.ColdDays(tree, index, now, trustAccessTimes);
            if (cold is null) return false;
            if (MinColdDays is not null && cold < MinColdDays) return false;
            if (MaxColdDays is not null && cold > MaxColdDays) return false;
        }

        ReadOnlySpan<byte> name = tree.GetNameBytes(index);

        if (_nameNeedle is not null && !ContainsAsciiInsensitive(name, _nameNeedle)) return false;

        if (_extensions.Length > 0)
        {
            if (e.IsDirectory) return false;
            if (!MatchesAnyExtension(name)) return false;
        }

        return true;
    }

    private bool MatchesAnyExtension(ReadOnlySpan<byte> name)
    {
        int dot = name.LastIndexOf((byte)'.');
        if (dot < 0 || dot == name.Length - 1) return false;
        ReadOnlySpan<byte> ext = name[(dot + 1)..];

        for (int i = 0; i < _extensions.Length; i++)
        {
            byte[] candidate = _extensions[i];
            if (candidate.Length != ext.Length) continue;

            bool equal = true;
            for (int j = 0; j < candidate.Length; j++)
            {
                if (ToLowerAscii(ext[j]) == candidate[j]) continue;
                equal = false;
                break;
            }
            if (equal) return true;
        }
        return false;
    }

    /// <summary>
    /// Teilstring-Suche direkt auf UTF-8, ohne Gross-/Kleinschreibung bei ASCII.
    /// Umlaute und andere Zeichen jenseits von ASCII werden exakt verglichen -
    /// fuer Dateinamen in der Praxis ausreichend und deutlich schneller als eine
    /// vollwertige Unicode-Faltung.
    /// </summary>
    private static bool ContainsAsciiInsensitive(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needleLower)
    {
        if (needleLower.Length == 0) return true;
        if (haystack.Length < needleLower.Length) return false;

        int limit = haystack.Length - needleLower.Length;
        byte first = needleLower[0];

        for (int i = 0; i <= limit; i++)
        {
            if (ToLowerAscii(haystack[i]) != first) continue;

            int j = 1;
            while (j < needleLower.Length && ToLowerAscii(haystack[i + j]) == needleLower[j]) j++;
            if (j == needleLower.Length) return true;
        }
        return false;
    }

    private static byte ToLowerAscii(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    private static byte[][] ParseExtensions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<byte[]>();

        string[] parts = text.Split(new[] { ',', ';', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<byte[]>(parts.Length);
        foreach (string part in parts)
        {
            string cleaned = part.Trim().TrimStart('*', '.').ToLowerInvariant();
            if (cleaned.Length > 0) result.Add(Encoding.UTF8.GetBytes(cleaned));
        }
        return result.ToArray();
    }

    /// <summary>Kurze Beschreibung fuer die Fusszeile und den Export-Kopf.</summary>
    public string Describe()
    {
        if (IsEmpty) return Loc.T("filter.none");

        var parts = new List<string>(5);
        if (MinSize > 0) parts.Add(Loc.F("filter.minSize", SizeFormat.Bytes(MinSize)));
        if (MinColdDays is not null) parts.Add(Loc.F("filter.minCold", MinColdDays));
        if (MaxColdDays is not null) parts.Add(Loc.F("filter.maxCold", MaxColdDays));
        if (Kind == EntryKind.DirectoriesOnly) parts.Add(Loc.T("filter.foldersOnly"));
        if (Kind == EntryKind.FilesOnly) parts.Add(Loc.T("filter.filesOnly"));
        if (NameContains is not null) parts.Add(Loc.F("filter.name", NameContains));
        if (_extensions.Length > 0) parts.Add(Loc.F("filter.extension", ExtensionText));
        if (ExcludeLinks) parts.Add(Loc.T("filter.noLinks"));
        return string.Join(", ", parts);
    }
}
