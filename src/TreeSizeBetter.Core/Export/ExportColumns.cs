using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Export;

public enum ExportValueType
{
    Text,
    Integer,
    Bytes,
    Decimal,
    DateTime
}

/// <summary>
/// Eine Spalte. Der Name ist die eine Quelle fuer Excel, CSV und JSON zugleich.
/// Die Ueberschrift kommt aus der Sprachtabelle ("col." + Key); der Key bleibt
/// sprachunabhaengig, damit JSON-Auswertungen nicht von der Sprache abhaengen.
/// </summary>
public sealed record ExportColumn(
    string Key,
    ExportValueType Type,
    Func<EntryView, object?> Get,
    double Width = 14)
{
    public string Header => Loc.T("col." + Key);
}

/// <summary>
/// Die Spaltendefinition aller Exporte an genau einer Stelle. Ohne das driften
/// Excel-, CSV- und JSON-Ausgabe frueher oder spaeter auseinander und niemand
/// merkt es, bis eine Auswertung falsche Zahlen liefert.
/// </summary>
public static class ExportColumns
{
    public static readonly IReadOnlyList<ExportColumn> All = new ExportColumn[]
    {
        new("path", ExportValueType.Text, v => v.FullPath, 70),
        new("name", ExportValueType.Text, v => v.Name, 32),
        new("type", ExportValueType.Text, v => v.TypeText, 16),
        new("extension", ExportValueType.Text, v => v.Extension, 10),
        new("category", ExportValueType.Text, v => EntryCategorizer.Describe(v.Category), 16),
        // Rohbytes zum Sortieren und Summieren, daneben die lesbare Form zum Ablesen.
        // Wird nach der Textspalte sortiert, kaeme "9 MB" vor "10 GB" - deshalb steht
        // die Zahlenspalte zuerst und traegt die Einheit im Namen.
        new("sizeBytes", ExportValueType.Bytes, v => v.Size, 16),
        new("size", ExportValueType.Text, v => SizeFormat.Bytes(v.Size), 12),
        new("sizeOnDiskBytes", ExportValueType.Bytes, v => v.SizeOnDisk, 16),
        new("fileCount", ExportValueType.Integer, v => v.IsDirectory ? v.FileCount : (object?)null, 11),
        new("dirCount", ExportValueType.Integer, v => v.IsDirectory ? v.DirCount : (object?)null, 11),
        new("created", ExportValueType.DateTime, v => v.Created, 18),
        new("modified", ExportValueType.DateTime, v => v.Modified, 18),
        new("accessed", ExportValueType.DateTime, v => v.Accessed, 18),
        new("modifiedDays", ExportValueType.Decimal, v => Round(v.ModifiedDays), 18),
        new("accessedDays", ExportValueType.Decimal, v => Round(v.AccessedDays), 20),
        new("coldDays", ExportValueType.Decimal, v => Round(v.ColdDays), 14),
        new("coldness", ExportValueType.Text, v => AgeAnalyzer.Describe(v.Coldness), 24),
        new("depth", ExportValueType.Integer, v => v.Depth, 8),
        new("attributes", ExportValueType.Text, v => v.AttributeText, 20),
        new("link", ExportValueType.Text, v => v.LinkKind, 16),
        new("relocatable", ExportValueType.Text, v => v.LooksRelocatable ? Loc.T("common.yes") : "", 12)
    };

    /// <summary>Kurze Spaltenliste fuer Ordneruebersichten - ohne die reinen Datei-Felder.</summary>
    public static readonly IReadOnlyList<ExportColumn> Folders = All
        .Where(c => c.Key is not ("extension" or "depth"))
        .ToArray();

    private static object? Round(double? value)
        => value is null ? null : Math.Round(value.Value, 1);
}
