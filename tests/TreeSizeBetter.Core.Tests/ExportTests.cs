using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Export;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;
using static TreeSizeBetter.Core.Tests.TreeBuilder;

namespace TreeSizeBetter.Core.Tests;

/// <summary>Legt einen Export-Kontext aus einem Testbaum an und raeumt die Dateien danach weg.</summary>
public sealed class ExportFixture : IDisposable
{
    public string Directory { get; }
    public ExportContext Context { get; }
    public ScanTree Tree { get; }

    public ExportFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "tsb-tests-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(Directory);

        Tree = Sample();
        var result = new ScanResult
        {
            Tree = Tree,
            ScannerName = "Test",
            StartedUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            Duration = TimeSpan.FromSeconds(1.5),
            FileCount = 3,
            DirectoryCount = 3,
            TotalBytes = 1015,
            TotalBytesOnDisk = 12288,
            BytesPerCluster = 4096,
            SizeOnDiskIsEstimate = true,
            VolumeTotalBytes = 1_000_000_000,
            VolumeFreeBytes = 400_000_000,
            Errors = new[] { new ScanError(@"T:\gesperrt", ScanErrorKind.AccessDenied, 5, "Zugriff verweigert") },
            DeniedCount = 1
        };

        AgeAnalysis age = AgeAnalyzer.Analyze(Tree, Time(2026, 1, 1));
        Context = new ExportContext
        {
            Result = result,
            Age = age,
            Overview = ScanOverview.Build(result, age)
        };
    }

    public string PathFor(string name) => Path.Combine(Directory, name);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Aufraeumen im Temp-Ordner ist nett, aber kein Grund, einen Test zu kippen.
        }
    }
}

public class ExportColumnTests
{
    [Fact]
    public void KeysAreUnique()
    {
        var keys = ExportColumns.All.Select(c => c.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void HeadersAreUnique()
    {
        var headers = ExportColumns.All.Select(c => c.Header).ToList();
        Assert.Equal(headers.Count, headers.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void FolderColumnsAreASubsetOfAll()
    {
        Assert.All(ExportColumns.Folders, column => Assert.Contains(column, ExportColumns.All));
        Assert.True(ExportColumns.Folders.Count < ExportColumns.All.Count);
    }

    [Fact]
    public void ReadableSizeComesAfterTheNumericOne()
    {
        // Sonst sortiert man versehentlich nach Text und 9 MB steht vor 10 GB.
        int numeric = IndexOfKey("sizeBytes");
        int readable = IndexOfKey("size");
        Assert.True(numeric < readable);
    }

    private static int IndexOfKey(string key)
    {
        for (int i = 0; i < ExportColumns.All.Count; i++)
            if (ExportColumns.All[i].Key == key) return i;
        throw new InvalidOperationException($"Spalte {key} fehlt.");
    }
}

public class CsvExporterTests : IClassFixture<ExportFixture>
{
    private readonly ExportFixture _fixture;

    public CsvExporterTests(ExportFixture fixture) => _fixture = fixture;

    [Fact]
    public void WritesHeaderAndOneRowPerEntry()
    {
        string path = _fixture.PathFor("basis.csv");
        ExportSummary summary = CsvExporter.Write(path, _fixture.Context);

        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal(summary.RowsWritten + 1, lines.Length);
        Assert.StartsWith("Pfad;Name;Typ", lines[0]);
        Assert.False(summary.WasCapped);
    }

    [Fact]
    public void WritesByteOrderMarkSoExcelReadsUmlautsCorrectly()
    {
        string path = _fixture.PathFor("bom.csv");
        CsvExporter.Write(path, _fixture.Context);

        byte[] head = File.ReadAllBytes(path).Take(3).ToArray();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, head);
    }

    [Fact]
    public void QuotesValuesThatContainTheDelimiter()
    {
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Time(2026, 1, 1), Time(2026, 1, 1), Time(2026, 1, 1));
        AddChildren(tree, ScanTree.RootIndex,
            FileEntry("heikel;name\"mit\"zeichen.txt", 10, Time(2025, 1, 1)));
        tree.Aggregate();

        ExportContext context = ContextFor(tree);
        string path = _fixture.PathFor("escaped.csv");
        CsvExporter.Write(path, context);

        string text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("\"heikel;name\"\"mit\"\"zeichen.txt\"", text);
    }

    [Fact]
    public void SizesAreRawBytesSoExcelCanSumThem()
    {
        string path = _fixture.PathFor("zahlen.csv");
        CsvExporter.Write(path, _fixture.Context);

        string text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains(";1000;", text);        // data.pak unformatiert
    }

    internal static ExportContext ContextFor(ScanTree tree)
    {
        var result = new ScanResult
        {
            Tree = tree,
            ScannerName = "Test",
            StartedUtc = DateTime.UtcNow,
            Duration = TimeSpan.FromSeconds(1),
            FileCount = 1,
            DirectoryCount = 0,
            TotalBytes = tree.SizeOf(ScanTree.RootIndex),
            BytesPerCluster = 4096
        };
        AgeAnalysis age = AgeAnalyzer.Analyze(tree, Time(2026, 1, 1));
        return new ExportContext
        {
            Result = result,
            Age = age,
            Overview = ScanOverview.Build(result, age)
        };
    }
}

public class JsonExporterTests : IClassFixture<ExportFixture>
{
    private readonly ExportFixture _fixture;

    public JsonExporterTests(ExportFixture fixture) => _fixture = fixture;

    [Fact]
    public void ProducesValidJsonWithMetadataAndEntries()
    {
        string path = _fixture.PathFor("daten.json");
        ExportSummary summary = JsonExporter.Write(path, _fixture.Context);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;

        Assert.Equal("SizeShift", root.GetProperty("meta").GetProperty("werkzeug").GetString());
        Assert.Equal(1015, root.GetProperty("summen").GetProperty("bytes").GetInt64());
        Assert.Equal(summary.RowsWritten, root.GetProperty("eintraege").GetArrayLength());
    }

    [Fact]
    public void NumbersAreWrittenAsNumbersNotText()
    {
        string path = _fixture.PathFor("typen.json");
        JsonExporter.Write(path, _fixture.Context);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement first = document.RootElement.GetProperty("eintraege")[0];

        Assert.Equal(JsonValueKind.Number, first.GetProperty("sizeBytes").ValueKind);
        Assert.Equal(JsonValueKind.String, first.GetProperty("path").ValueKind);
    }
}

public class XlsxExporterTests : IClassFixture<ExportFixture>
{
    private readonly ExportFixture _fixture;

    public XlsxExporterTests(ExportFixture fixture) => _fixture = fixture;

    [Fact]
    public void ProducesAWorkbookExcelCanOpenWithAllSheets()
    {
        string path = _fixture.PathFor("mappe.xlsx");
        XlsxExporter.Write(path, _fixture.Context);

        using var workbook = new XLWorkbook(path);
        var names = workbook.Worksheets.Select(w => w.Name).ToList();

        Assert.Equal(
            new[] { "Übersicht", "Ordner", "Dateien", "Dateitypen", "Alter", "Umlagern", "Meldungen" },
            names);
    }

    [Fact]
    public void FileSheetHasOneHeaderRowAndOneRowPerFile()
    {
        string path = _fixture.PathFor("zeilen.xlsx");
        XlsxExporter.Write(path, _fixture.Context);

        using var workbook = new XLWorkbook(path);
        IXLWorksheet sheet = workbook.Worksheet("Dateien");

        Assert.Equal("Pfad", sheet.Cell(1, 1).GetString());
        Assert.Equal(4, sheet.LastRowUsed()!.RowNumber());      // Kopfzeile + 3 Dateien
    }

    [Fact]
    public void SizesAreStoredAsNumbersSoExcelCanSortAndSum()
    {
        string path = _fixture.PathFor("formate.xlsx");
        XlsxExporter.Write(path, _fixture.Context);

        using var workbook = new XLWorkbook(path);
        IXLWorksheet sheet = workbook.Worksheet("Dateien");

        int sizeColumn = ExportColumns.All.Select((c, i) => (c, i)).First(x => x.c.Key == "sizeBytes").i + 1;
        IXLCell cell = sheet.Cell(2, sizeColumn);

        Assert.Equal(XLDataType.Number, cell.DataType);
        Assert.Equal(1000, cell.GetDouble());                   // groesste Datei zuerst
    }

    [Fact]
    public void MessagesSheetCarriesTheScanErrors()
    {
        string path = _fixture.PathFor("meldungen.xlsx");
        XlsxExporter.Write(path, _fixture.Context);

        using var workbook = new XLWorkbook(path);
        IXLWorksheet sheet = workbook.Worksheet("Meldungen");

        Assert.Equal("AccessDenied", sheet.Cell(2, 1).GetString());
        Assert.Equal(@"T:\gesperrt", sheet.Cell(2, 2).GetString());
    }
}

public class AiReportTests : IClassFixture<ExportFixture>
{
    private readonly ExportFixture _fixture;

    public AiReportTests(ExportFixture fixture) => _fixture = fixture;

    [Fact]
    public void ContainsTheSectionsAnAssistantNeedsToDecide()
    {
        string report = AiReportExporter.Build(_fixture.Context);

        Assert.Contains("## Eckdaten", report);
        Assert.Contains("## Wie belastbar sind die Altersangaben", report);
        Assert.Contains("## Altersverteilung", report);
        Assert.Contains("## Ordnerbaum", report);
        Assert.Contains("## Auffälligkeiten", report);
        Assert.Contains("## Worum es geht", report);
    }

    [Fact]
    public void StatesWhetherAccessTimesCanBeTrusted()
    {
        string report = AiReportExporter.Build(_fixture.Context);
        Assert.Contains("Zugriffszeiten sind", report);
    }

    [Fact]
    public void RespectsTheLineBudget()
    {
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Time(2026, 1, 1), Time(2026, 1, 1), Time(2026, 1, 1));

        // Breiter Baum, der ohne Budget hunderte Zeilen erzeugen wuerde.
        var folders = new List<(string, RawChild)>();
        for (int i = 0; i < 400; i++) folders.Add(Dir($"ordner{i:D3}"));
        int first = AddChildren(tree, ScanTree.RootIndex, folders.ToArray());
        for (int i = 0; i < 400; i++)
            AddChildren(tree, first + i, FileEntry($"gross{i}.bin", 50L * 1024 * 1024, Time(2024, 1, 1)));
        tree.Aggregate();

        string report = AiReportExporter.Build(
            CsvExporterTests.ContextFor(tree), new AiReportOptions { MaxTreeLines = 50 });

        int treeLines = report.Split('\n').Count(l => l.TrimStart().StartsWith("ordner"));
        Assert.True(treeLines <= 50, $"Baum hatte {treeLines} Zeilen trotz Budget 50.");
        Assert.Contains("weitere Ordner", report);
    }

    [Fact]
    public void EscapesPipesSoTheMarkdownTablesStayIntact()
    {
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Time(2026, 1, 1), Time(2026, 1, 1), Time(2026, 1, 1));
        AddChildren(tree, ScanTree.RootIndex, FileEntry("mit|strich.bin", 200L * 1024 * 1024, Time(2024, 1, 1)));
        tree.Aggregate();

        string report = AiReportExporter.Build(CsvExporterTests.ContextFor(tree));
        Assert.Contains(@"mit\|strich.bin", report);
    }
}

public class EntryCategoryTests
{
    [Theory]
    [InlineData(@"D:\Spiele\Steam\steamapps\common\Apex Legends", true, EntryCategory.Spiel)]
    [InlineData(@"C:\Windows\System32\drivers", true, EntryCategory.System)]
    [InlineData(@"C:\Users\alex\AppData\Local\Temp\foo", true, EntryCategory.Cache)]
    [InlineData(@"C:\Users\alex\Downloads\setup.msi", false, EntryCategory.Installer)]
    [InlineData(@"D:\Bilder\urlaub.jpg", false, EntryCategory.Medien)]
    [InlineData(@"D:\Dokumente\vertrag.pdf", false, EntryCategory.Dokumente)]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21", true, EntryCategory.Papierkorb)]
    public void RecognisesCommonLayouts(string path, bool isDirectory, EntryCategory expected)
        => Assert.Equal(expected, EntryCategorizer.Categorize(path, isDirectory, isReparsePoint: false));

    [Fact]
    public void SystemBeatsCacheSoWindowsFoldersAreNeverTreatedAsDisposable()
    {
        // Ein Cache unter C:\Windows bleibt ein Systemordner - sonst landet er
        // in Vorschlaegen, in denen er nichts zu suchen hat.
        Assert.Equal(EntryCategory.System,
            EntryCategorizer.Categorize(@"C:\Windows\SoftwareDistribution\Download", true, false));
    }

    [Fact]
    public void ReparsePointsAreTheirOwnCategory()
        => Assert.Equal(EntryCategory.Verknuepfung,
            EntryCategorizer.Categorize(@"D:\Spiele\Steam", true, isReparsePoint: true));

    [Fact]
    public void OnlyMovableKindsAreSuggestedForRelocation()
    {
        Assert.True(EntryCategorizer.LooksRelocatable(EntryCategory.Spiel));
        Assert.True(EntryCategorizer.LooksRelocatable(EntryCategory.Medien));
        Assert.False(EntryCategorizer.LooksRelocatable(EntryCategory.System));
        Assert.False(EntryCategorizer.LooksRelocatable(EntryCategory.Cache));
        Assert.False(EntryCategorizer.LooksRelocatable(EntryCategory.Verknuepfung));
    }
}

public class ExportEstimatorTests : IClassFixture<ExportFixture>
{
    private readonly ExportFixture _fixture;

    public ExportEstimatorTests(ExportFixture fixture) => _fixture = fixture;

    [Fact]
    public void EstimateMatchesTheWrittenFiles()
    {
        ExportEstimate estimate = ExportEstimator.Estimate(_fixture.Context);
        ExportSummary csv = CsvExporter.Write(_fixture.PathFor("schaetzung.csv"), _fixture.Context);
        ExportSummary json = JsonExporter.Write(_fixture.PathFor("schaetzung.json"), _fixture.Context);

        Assert.Equal(csv.RowsWritten, estimate.Rows);
        Assert.Equal(estimate.Rows, estimate.Folders + estimate.Files);
        // Bei so wenigen Zeilen ist jede Zeile in der Stichprobe - die CSV-Schaetzung muss genau sein.
        Assert.Equal(csv.FileSizeBytes, estimate.CsvBytes);
        // JSON: Zeilen exakt, Kopfteil pauschal geschaetzt.
        Assert.InRange(estimate.JsonBytes - json.FileSizeBytes, -4096, 4096);
    }

    [Fact]
    public void FiltersAreRespected()
    {
        ExportContext onlyFolders = new()
        {
            Result = _fixture.Context.Result,
            Age = _fixture.Context.Age,
            Overview = _fixture.Context.Overview,
            Filter = new EntryFilter(kind: EntryKind.DirectoriesOnly)
        };
        ExportEstimate estimate = ExportEstimator.Estimate(onlyFolders);
        Assert.Equal(0, estimate.Files);
        Assert.Equal(estimate.Rows, estimate.Folders);
    }
}
