using System.Diagnostics;
using TreeSizeBetter.Core.Offload;

namespace TreeSizeBetter.Core.Tests;

/// <summary>Arbeitet mit echten Junctions in einem Temp-Ordner - kein Mocking.</summary>
public sealed class JunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsb-junction-" + Guid.NewGuid().ToString("N")[..8]);

    public JunctionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CreatedJunctionIsTransparentForReadersAndWriters()
    {
        string target = Path.Combine(_root, "ziel");
        string link = Path.Combine(_root, "verknuepfung");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.txt"), "hallo");

        JunctionManager.Create(link, target);

        // Lesen durch die Junction ...
        Assert.Equal("hallo", File.ReadAllText(Path.Combine(link, "a.txt")));
        // ... und Schreiben durch sie landet im Ziel.
        File.WriteAllText(Path.Combine(link, "b.txt"), "welt");
        Assert.True(File.Exists(Path.Combine(target, "b.txt")));
    }

    [Fact]
    public void ReadTargetReturnsTheTarget()
    {
        string target = Path.Combine(_root, "ziel");
        string link = Path.Combine(_root, "verknuepfung");
        Directory.CreateDirectory(target);

        JunctionManager.Create(link, target);

        Assert.Equal(target, JunctionManager.ReadTarget(link), ignoreCase: true);
        Assert.True(JunctionManager.IsJunctionTo(link, target));
    }

    [Fact]
    public void WindowsItselfSeesAMountPoint()
    {
        // Gegenprobe mit Bordmitteln statt mit dem eigenen Lesecode.
        string target = Path.Combine(_root, "ziel");
        string link = Path.Combine(_root, "verknuepfung");
        Directory.CreateDirectory(target);
        JunctionManager.Create(link, target);

        var info = new ProcessStartInfo("fsutil", $"reparsepoint query \"{link}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Contains("0xa0000003", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoveDeletesOnlyTheLinkNeverTheTarget()
    {
        string target = Path.Combine(_root, "ziel");
        string link = Path.Combine(_root, "verknuepfung");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "wichtig.txt"), "bleibt");

        JunctionManager.Create(link, target);
        JunctionManager.Remove(link);

        Assert.False(Directory.Exists(link));
        Assert.Equal("bleibt", File.ReadAllText(Path.Combine(target, "wichtig.txt")));
    }

    [Fact]
    public void RemoveRefusesARealFolder()
    {
        string real = Path.Combine(_root, "echt");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "x.txt"), "x");

        Assert.Throws<IOException>(() => JunctionManager.Remove(real));
        Assert.True(File.Exists(Path.Combine(real, "x.txt")));
    }

    [Fact]
    public void CreateRefusesAnExistingPath()
    {
        string target = Path.Combine(_root, "ziel");
        string occupied = Path.Combine(_root, "belegt");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(occupied);

        Assert.Throws<IOException>(() => JunctionManager.Create(occupied, target));
    }

    [Fact]
    public void ReadTargetIsNullForAPlainFolder()
    {
        string real = Path.Combine(_root, "echt");
        Directory.CreateDirectory(real);
        Assert.Null(JunctionManager.ReadTarget(real));
    }

    public void Dispose()
    {
        // Junctions zuerst loesen, damit das rekursive Loeschen nie durch sie hindurch laeuft.
        foreach (string dir in Directory.EnumerateDirectories(_root))
            if (JunctionManager.ReadTarget(dir) is not null) Directory.Delete(dir, recursive: false);
        Directory.Delete(_root, recursive: true);
    }
}
