using Microsoft.Win32;
using TreeSizeBetter.Core.SystemInfo;

namespace TreeSizeBetter.Core.Tests;

/// <summary>
/// Autostart lesen und schalten - ausschliesslich in einem eigenen Testschluessel
/// unter HKCU\Software\SizeShiftTests und in Temp-Ordnern. Der echte Autostart
/// dieses PCs wird nie angefasst.
/// </summary>
public sealed class AutostartTests : IDisposable
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private readonly string _testRoot = @"Software\SizeShiftTests\" + Guid.NewGuid().ToString("N")[..8];
    private readonly RegistryKey _user;
    private readonly RegistryKey _machine;
    private readonly string _userFolder;
    private readonly string _commonFolder;
    private readonly AutostartManager _manager;

    public AutostartTests()
    {
        _user = Registry.CurrentUser.CreateSubKey(_testRoot + @"\hkcu");
        _machine = Registry.CurrentUser.CreateSubKey(_testRoot + @"\hklm");
        string temp = Path.Combine(Path.GetTempPath(), "tsb-startup-" + Guid.NewGuid().ToString("N")[..8]);
        _userFolder = Path.Combine(temp, "user");
        _commonFolder = Path.Combine(temp, "common");
        Directory.CreateDirectory(_userFolder);
        Directory.CreateDirectory(_commonFolder);
        _manager = new AutostartManager(_user, _machine, _userFolder, _commonFolder);
    }

    [Fact]
    public void ReadsRegistryEntriesAsOnWithoutApprovedValue()
    {
        using (RegistryKey run = _user.CreateSubKey(Run)) run.SetValue("Tool", "\"C:\\Programme\\Tool\\tool.exe\" /min");

        AutostartEntry entry = Assert.Single(_manager.ReadAll());
        Assert.Equal("Tool", entry.Name);
        Assert.True(entry.Enabled);
        Assert.Equal(AutostartLocation.BenutzerRegistry, entry.Location);
        Assert.Equal(@"C:\Programme\Tool\tool.exe", entry.TargetPath);
        Assert.True(entry.FileMissing);   // gibt es auf dem Testrechner nicht
        Assert.False(entry.NeedsAdmin);
    }

    [Fact]
    public void SwitchingOffKeepsTheEntryAndWritesTheTaskManagerFlag()
    {
        string exe = Path.Combine(_userFolder, "..", "tool.exe");
        File.WriteAllText(exe, "x");
        using (RegistryKey run = _user.CreateSubKey(Run)) run.SetValue("Tool", $"\"{exe}\"");

        AutostartEntry entry = Assert.Single(_manager.ReadAll());
        Assert.Null(_manager.SetEnabled(entry, enabled: false));

        AutostartEntry off = Assert.Single(_manager.ReadAll());
        Assert.False(off.Enabled);
        Assert.False(off.FileMissing);

        // Der Run-Eintrag selbst ist unveraendert, nur das Kennzeichen kam dazu.
        using (RegistryKey run = _user.OpenSubKey(Run)!) Assert.Equal($"\"{exe}\"", run.GetValue("Tool"));
        using (RegistryKey approved = _user.OpenSubKey(Approved + @"\Run")!)
        {
            byte[] data = (byte[])approved.GetValue("Tool")!;
            Assert.Equal(12, data.Length);
            Assert.Equal(0x03, data[0]);
            Assert.NotEqual(0L, BitConverter.ToInt64(data, 4));   // Zeitpunkt des Abschaltens
        }

        Assert.Null(_manager.SetEnabled(off, enabled: true));
        Assert.True(Assert.Single(_manager.ReadAll()).Enabled);
    }

    [Fact]
    public void MachineEntriesNeedAdminAndUseTheirOwnApprovedKey()
    {
        using (RegistryKey run = _machine.CreateSubKey(@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"))
            run.SetValue("Alt32", @"C:\alt\alt.exe");

        AutostartEntry entry = Assert.Single(_manager.ReadAll());
        Assert.Equal(AutostartLocation.AlleRegistry32, entry.Location);
        Assert.True(entry.NeedsAdmin);

        Assert.Null(_manager.SetEnabled(entry, enabled: false));
        using RegistryKey approved = _machine.OpenSubKey(Approved + @"\Run32")!;
        Assert.Equal(0x03, ((byte[])approved.GetValue("Alt32")!)[0]);
    }

    [Fact]
    public void ReadsStartupFolderFilesAndHonoursTheirFlag()
    {
        string file = Path.Combine(_userFolder, "Notiz.bat");
        File.WriteAllText(file, "@echo off");
        using (RegistryKey approved = _user.CreateSubKey(Approved + @"\StartupFolder"))
            approved.SetValue("Notiz.bat", new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);

        AutostartEntry entry = Assert.Single(_manager.ReadAll());
        Assert.Equal("Notiz", entry.Name);
        Assert.Equal(AutostartLocation.BenutzerOrdner, entry.Location);
        Assert.False(entry.Enabled);

        Assert.Null(_manager.SetEnabled(entry, enabled: true));
        Assert.True(Assert.Single(_manager.ReadAll()).Enabled);
        Assert.True(File.Exists(file));   // die Datei bleibt immer liegen
    }

    [Fact]
    public void ReadsAndSwitchesStoreAppStartupTasks()
    {
        const string tasks = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
        const string packages = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        using (RegistryKey t = _user.CreateSubKey(tasks + @"\Contoso.Chat_abc123\ChatStartup")) t.SetValue("State", 2, RegistryValueKind.DWord);
        using (RegistryKey t = _user.CreateSubKey(tasks + @"\Contoso.Sync_abc123\SyncA")) t.SetValue("State", 0, RegistryValueKind.DWord);
        using (RegistryKey t = _user.CreateSubKey(tasks + @"\Contoso.Sync_abc123\SyncB")) t.SetValue("State", 3, RegistryValueKind.DWord);
        using (RegistryKey p = _user.CreateSubKey(packages + @"\Contoso.Chat_1.2.0.0_x64__abc123")) p.SetValue("DisplayName", "Contoso Chat");

        IReadOnlyList<AutostartEntry> all = _manager.ReadAll();
        Assert.Equal(3, all.Count);

        AutostartEntry chat = Assert.Single(all, e => e.Name == "Contoso Chat");
        Assert.Equal(AutostartLocation.StoreApp, chat.Location);
        Assert.True(chat.Enabled);
        Assert.False(chat.NeedsAdmin);

        // Mehrere Aufgaben eines Pakets: Name mit Aufgabe; ohne Repository-Eintrag der Paketname.
        Assert.Contains(all, e => e.Name == "Contoso.Sync (SyncA)" && !e.Enabled && !e.PolicyLocked);
        Assert.Contains(all, e => e.Name == "Contoso.Sync (SyncB)" && e.PolicyLocked);

        Assert.Null(_manager.SetEnabled(chat, enabled: false));
        using (RegistryKey t = _user.OpenSubKey(tasks + @"\Contoso.Chat_abc123\ChatStartup")!)
            Assert.Equal(1, t.GetValue("State"));   // 1 = vom Benutzer abgeschaltet
        Assert.False(Assert.Single(_manager.ReadAll(), e => e.Name == "Contoso Chat").Enabled);

        // Per Richtlinie gesperrt: nicht schaltbar.
        AutostartEntry locked = Assert.Single(_manager.ReadAll(), e => e.PolicyLocked);
        Assert.NotNull(_manager.SetEnabled(locked, enabled: true));
    }

    [Theory]
    [InlineData("\"C:\\A B\\x.exe\" --flag", @"C:\A B\x.exe")]
    [InlineData(@"C:\Tools\app.exe -silent", @"C:\Tools\app.exe")]
    [InlineData(@"C:\Tools\script.cmd", @"C:\Tools\script.cmd")]
    public void FindsTheExecutableInACommandLine(string command, string expected)
        => Assert.Equal(expected, AutostartManager.ExecutableOf(command));

    public void Dispose()
    {
        _user.Dispose();
        _machine.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_testRoot, throwOnMissingSubKey: false);
        using (RegistryKey? parent = Registry.CurrentUser.OpenSubKey(@"Software\SizeShiftTests", writable: true))
        {
            if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
                Registry.CurrentUser.DeleteSubKey(@"Software\SizeShiftTests", throwOnMissingSubKey: false);
        }
        Directory.Delete(Path.GetDirectoryName(_userFolder)!, recursive: true);
    }
}
