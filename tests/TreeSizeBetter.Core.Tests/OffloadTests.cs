using TreeSizeBetter.Core.Offload;

namespace TreeSizeBetter.Core.Tests;

public sealed class DeletePermanentlyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsb-delete-" + Guid.NewGuid().ToString("N")[..8]);

    public DeletePermanentlyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void RemovesReadOnlyFilesToo()
    {
        // Genau der Fall, der im echten Testlauf liegengeblieben ist.
        string folder = Path.Combine(_root, "original");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        string config = Path.Combine(folder, "sub", "config.ini");
        File.WriteAllText(config, "x");
        File.SetAttributes(config, FileAttributes.ReadOnly);

        OffloadEngine.DeletePermanently(folder);

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void NeverDeletesThroughANestedJunction()
    {
        string outside = Path.Combine(_root, "draussen");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "muss-bleiben.txt"), "x");
        File.SetAttributes(Path.Combine(outside, "muss-bleiben.txt"), FileAttributes.ReadOnly);

        string folder = Path.Combine(_root, "original");
        Directory.CreateDirectory(folder);
        JunctionManager.Create(Path.Combine(folder, "link"), outside);

        OffloadEngine.DeletePermanently(folder);

        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(outside, "muss-bleiben.txt")));
        // Auch der Schreibschutz der Datei hinter der Junction blieb unangetastet.
        Assert.True(new FileInfo(Path.Combine(outside, "muss-bleiben.txt")).IsReadOnly);
    }

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}

public sealed class RelocationGuardTests
{
    [Theory]
    [InlineData(@"C:\Users\alex\curseforge", @"D:\Ausgelagert", @"D:\Ausgelagert\C\Users\alex\curseforge")]
    [InlineData(@"C:\Spiele\Anno", @"E:\Archiv\", @"E:\Archiv\C\Spiele\Anno")]
    public void TargetMirrorsTheSourcePath(string source, string root, string expected)
        => Assert.Equal(expected, RelocationGuard.BuildTargetPath(source, root), ignoreCase: true);

    [Fact]
    public void RefusesTheSameDrive()
    {
        string source = Path.Combine(Path.GetTempPath(), "tsb-guard-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(source);
        try
        {
            string sameDrive = Path.GetPathRoot(source)! + "Irgendwo";
            RelocationCheck check = RelocationGuard.Check(source, RelocationGuard.TargetInFolder(source, sameDrive));
            Assert.Contains(check.Issues, i => i.Severity == IssueSeverity.Sperre && i.Message.Contains("demselben Laufwerk"));
        }
        finally
        {
            Directory.Delete(source);
        }
    }

    [Fact]
    public void RefusesAWholeDrive()
    {
        RelocationCheck check = RelocationGuard.Check(@"C:\", @"D:\Ziel\C");
        Assert.True(check.IsBlocked);
    }

    [Fact]
    public void RefusesWindows()
    {
        RelocationCheck check = RelocationGuard.Check(@"C:\Windows\Web", @"D:\Ziel\Web");
        Assert.Contains(check.Issues, i => i.Severity == IssueSeverity.Sperre && i.Message.Contains("Systembereich"));
    }

    [Theory]
    [InlineData(@"C:\Games\Witcher", @"D:\Spiele", @"D:\Spiele\Witcher")]
    [InlineData(@"C:\Games\Witcher\", @"D:\", @"D:\Witcher")]
    public void TargetInFolderKeepsTheFolderName(string source, string folder, string expected)
        => Assert.Equal(expected, RelocationGuard.TargetInFolder(source, folder), ignoreCase: true);

    [Fact]
    public void RefusesWhenTheTargetNameIsTaken()
    {
        string root = Path.Combine(Path.GetTempPath(), "tsb-guard-" + Guid.NewGuid().ToString("N")[..8]);
        string source = Path.Combine(root, "Quelle", "Spiel");
        string target = Path.Combine(root, "Ziel", "Spiel");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "da.txt"), "x");
        try
        {
            RelocationCheck check = RelocationGuard.Check(source, target);
            Assert.Contains(check.Issues, i => i.Severity == IssueSeverity.Sperre && i.Message.Contains("bereits ein Ordner"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class OffloadJournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "tsb-journal-" + Guid.NewGuid().ToString("N")[..8] + ".db");

    [Fact]
    public void RemembersEveryStepAcrossReopening()
    {
        long id;
        using (var journal = new OffloadJournal(_path))
        {
            id = journal.Begin(@"C:\a", @"D:\x\C\a", 1234, 5, DeletionMode.Papierkorb);
            journal.SetStatus(id, RelocationStatus.Pruefen);
        }

        // Neu geoeffnet, wie nach einem Absturz: der Zwischenstand ist noch da.
        using (var journal = new OffloadJournal(_path))
        {
            JournalEntry entry = journal.Get(id)!;
            Assert.Equal(RelocationStatus.Pruefen, entry.Status);
            Assert.Single(journal.Unsettled());

            journal.SetStatus(id, RelocationStatus.Fertig, "ok", finished: true);
            Assert.Empty(journal.Unsettled());
            Assert.True(journal.Get(id)!.CanUndo);
        }
    }

    [Fact]
    public async Task AdoptsAJunctionWithoutEntryOnlyOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "tsb-adopt-" + Guid.NewGuid().ToString("N")[..8]);
        string data = Path.Combine(root, "daten");
        string link = Path.Combine(root, "link");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "a.txt"), "hallo");
        JunctionManager.Create(link, data);
        try
        {
            using var journal = new OffloadJournal(_path);
            var engine = new OffloadEngine(journal);

            OffloadOutcome first = await engine.AdoptAsync(link, CancellationToken.None);
            Assert.True(first.Success);
            JournalEntry entry = journal.Get(first.JournalId!.Value)!;
            Assert.True(entry.CanUndo);
            Assert.Equal(link, entry.Source, ignoreCase: true);
            Assert.Equal(data, entry.Target, ignoreCase: true);

            // Zweiter Versuch legt keinen doppelten Eintrag an.
            OffloadOutcome second = await engine.AdoptAsync(link, CancellationToken.None);
            Assert.Equal(first.JournalId, second.JournalId);
            Assert.Single(journal.All());
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefusesToAdoptAPlainFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "tsb-adopt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            using var journal = new OffloadJournal(_path);
            OffloadOutcome outcome = await new OffloadEngine(journal).AdoptAsync(folder, CancellationToken.None);
            Assert.False(outcome.Success);
            Assert.Empty(journal.All());
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
