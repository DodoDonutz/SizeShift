using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Cleanup;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Offload;
using Xunit;
using static TreeSizeBetter.Core.Tests.TreeBuilder;

namespace TreeSizeBetter.Core.Tests;

public class CleanupScannerTests
{
    private const long MB = 1024 * 1024;
    private static readonly long Now = Time(2026, 1, 10);
    private static readonly long Old = Time(2024, 3, 1);
    private static readonly long Fresh = Time(2026, 1, 9);

    private static readonly KnownFolders Folders = new(
        SystemDrive: "T:",
        Windows: @"T:\Windows",
        ProgramData: @"T:\ProgramData",
        UserProfile: @"T:\Users\me",
        LocalAppData: @"T:\Users\me\AppData\Local",
        RoamingAppData: @"T:\Users\me\AppData\Roaming");

    private static IReadOnlyList<CleanupRule> Rules => CleanupCatalog.Build(Folders);

    /// <summary>Baut einen Baum aus Dateipfaden. Ordner entstehen von selbst; "dir\" am Ende legt einen leeren an.</summary>
    private static ScanTree Build(string root, params (string Path, long Size, long Modified)[] files)
    {
        var top = new Node("", true, 0, Old);
        foreach (var (path, size, modified) in files)
        {
            string[] parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            Node current = top;
            for (int i = 0; i < parts.Length; i++)
            {
                bool isDir = i < parts.Length - 1 || path.EndsWith('\\');
                if (!current.Children.TryGetValue(parts[i], out Node? next))
                    current.Children[parts[i]] = next = new Node(parts[i], isDir, isDir ? 0 : size, isDir ? Old : modified);
                current = next;
            }
        }

        var tree = new ScanTree();
        tree.CreateRoot(root, 0, Old, Old, Old);
        var queue = new Queue<(Node Node, int Index)>();
        queue.Enqueue((top, ScanTree.RootIndex));
        while (queue.Count > 0)
        {
            var (node, index) = queue.Dequeue();
            if (node.Children.Count == 0) continue;
            var children = node.Children.Values.ToList();
            int first = AddChildren(tree, index, children.Select(c => c.IsDir
                ? (c.Name, Dir(c.Name).Child)
                : FileEntry(c.Name, c.Size, c.Modified)).ToArray());
            for (int i = 0; i < children.Count; i++) queue.Enqueue((children[i], first + i));
        }
        tree.Aggregate();
        return tree;
    }

    private sealed record Node(string Name, bool IsDir, long Size, long Modified)
    {
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static List<CleanupFinding> Find(ScanTree tree) => CleanupScanner.Find(tree, Rules, Now);

    // ------------------------------------------------------------ Privatsphaere

    /// <summary>Dateien, die keine Regel je treffen darf: Passwoerter, Lesezeichen, Erweiterungen, Einstellungen.</summary>
    private static readonly string[] Untouchable =
    {
        "Login Data", "Login Data-journal", "Login Data For Account", "Bookmarks", "Bookmarks.bak", "Preferences",
        "Secure Preferences", "Web Data", @"Extensions\abc\1.0\manifest.json", @"Local Extension Settings\abc\000003.log",
        "places.sqlite", "logins.json", "key4.db", "prefs.js", "favicons.sqlite", @"extensions\ublock@raymondhill.net.xpi"
    };

    [Fact]
    public void Privacy_rules_never_touch_passwords_bookmarks_extensions_or_settings()
    {
        string chrome = @"Users\me\AppData\Local\Google\Chrome\User Data\Default\";
        string edge = @"Users\me\AppData\Local\Microsoft\Edge\User Data\Profile 1\";
        string firefox = @"Users\me\AppData\Roaming\Mozilla\Firefox\Profiles\x1.default\";
        var files = new List<(string, long, long)>();
        foreach (string profile in new[] { chrome, edge, firefox })
        {
            foreach (string name in Untouchable) files.Add((profile + name, MB, Old));
            files.Add((profile + "History", MB, Old));
            files.Add((profile + @"Network\Cookies", MB, Old));
            files.Add((profile + "cookies.sqlite", MB, Old));
            files.Add((profile + "formhistory.sqlite", MB, Old));
        }

        List<CleanupFinding> findings = Find(Build(@"T:\", files.ToArray()));
        IEnumerable<string> touched = findings.Where(f => f.Rule.IsPrivacy).SelectMany(f => f.Items).Select(i => i.Path);

        foreach (string path in touched)
            Assert.DoesNotContain(Untouchable, name => path.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase));

        // Und das Gewollte wird gefunden.
        Assert.Contains(findings, f => f.Rule.Id == "privacy-history-chrome" && f.Items.Any(i => i.Path.EndsWith(@"Default\History")));
        Assert.Contains(findings, f => f.Rule.Id == "privacy-cookies-edge" && f.Items.Any(i => i.Path.EndsWith(@"Network\Cookies")));
        Assert.Contains(findings, f => f.Rule.Id == "privacy-cookies-firefox");
        Assert.Contains(findings, f => f.Rule.Id == "privacy-forms-firefox");
    }

    [Fact]
    public void Privacy_rules_do_not_block_cache_rules_and_vice_versa()
    {
        string profile = @"Users\me\AppData\Local\Google\Chrome\User Data\Default\";
        List<CleanupFinding> findings = Find(Build(@"T:\",
            (profile + @"Cache\Cache_Data\f_000001", 50 * MB, Old),
            (profile + "History", MB, Old)));

        Assert.Contains(findings, f => f.Rule.Id == "browser-chrome");
        CleanupFinding history = Assert.Single(findings, f => f.Rule.Id == "privacy-history-chrome");
        Assert.Single(history.Items);   // nur History, nicht der Cache darunter
    }

    [Fact]
    public void Clipboard_and_dns_appear_once_with_the_system_drive_and_never_preselected_fields()
    {
        List<CleanupFinding> findings = Find(Build(@"T:\", (@"Windows\notepad.exe", MB, Old)));

        CleanupFinding clipboard = Assert.Single(findings, f => f.Rule.Id == "privacy-clipboard");
        CleanupFinding dns = Assert.Single(findings, f => f.Rule.Id == "privacy-dns");
        Assert.Empty(clipboard.Items);
        Assert.True(clipboard.Rule.IsVirtual && dns.Rule.IsVirtual);
        Assert.Equal(dns.Rule.DisplayLocation, dns.Location);

        // Ohne Systemlaufwerk im Scan (z. B. D:) gibt es sie nicht.
        Assert.DoesNotContain(Find(Build(@"T:\", (@"Spiele\x.bin", MB, Old))), f => f.Rule.IsVirtual);
    }

    [Fact]
    public void Recent_files_and_thumbnails_are_found_by_name_only()
    {
        List<CleanupFinding> findings = Find(Build(@"T:\",
            (@"Users\me\AppData\Roaming\Microsoft\Windows\Recent\Brief.docx.lnk", 4096, Old),
            (@"Users\me\AppData\Roaming\Microsoft\Windows\Recent\AutomaticDestinations\abc.automaticDestinations-ms", 4096, Old),
            (@"Users\me\AppData\Local\Microsoft\Windows\Explorer\thumbcache_256.db", 30 * MB, Old),
            (@"Users\me\AppData\Local\Microsoft\Windows\Explorer\iconcache_32.db", 2 * MB, Old)));

        Assert.Equal(2, Assert.Single(findings, f => f.Rule.Id == "privacy-recent").Items.Count);
        CleanupFinding thumbs = Assert.Single(findings, f => f.Rule.Id == "privacy-thumbnails");
        Assert.Equal("thumbcache_256.db", Path.GetFileName(Assert.Single(thumbs.Items).Path));
    }

    [Theory]
    [InlineData("History", "History", true)]
    [InlineData(@"Network\Cookies", @"Network\Cookies", true)]
    [InlineData(@"Sessions\Session_1334", @"Sessions\*", true)]
    [InlineData("thumbcache_1280.db", "thumbcache_*.db", true)]
    [InlineData(@"Sub\History", "History", false)]
    [InlineData("Login Data", "*Data-journal", false)]
    public void Wildcards_match_per_path_segment(string path, string pattern, bool expected)
        => Assert.Equal(expected, TreeSizeBetter.Core.Util.Wildcard.IsMatch(path, pattern));

    [Fact]
    public void Temp_keeps_recent_items_and_counts_them_separately()
    {
        ScanTree tree = Build(@"T:\",
            (@"Users\me\AppData\Local\Temp\old.tmp", 2 * MB, Old),
            (@"Users\me\AppData\Local\Temp\setup\a.bin", 3 * MB, Old),
            (@"Users\me\AppData\Local\Temp\fresh.tmp", 5 * MB, Fresh));

        CleanupFinding temp = Assert.Single(Find(tree), f => f.Rule.Id == "temp-user");

        Assert.Equal(new[] { "old.tmp", "setup" }, temp.Items.Select(i => Path.GetFileName(i.Path)).OrderBy(n => n));
        Assert.Equal(5 * MB, temp.Bytes);
        Assert.Equal(5 * MB, temp.KeptBytes);
        Assert.Equal(@"T:\Users\me\AppData\Local\Temp", temp.Location);
    }

    [Fact]
    public void Wildcard_finds_every_browser_profile_in_one_finding()
    {
        ScanTree tree = Build(@"T:\",
            (@"Users\me\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f_1", 2 * MB, Old),
            (@"Users\me\AppData\Local\Google\Chrome\User Data\Profile 1\Cache\Cache_Data\f_1", 2 * MB, Old),
            (@"Users\me\AppData\Local\Google\Chrome\User Data\Default\Bookmarks", 2 * MB, Old));

        CleanupFinding chrome = Assert.Single(Find(tree), f => f.Rule.Id == "browser-chrome");

        Assert.Equal(2, chrome.Roots.Count);
        Assert.Equal(4 * MB, chrome.Bytes);
        Assert.DoesNotContain(chrome.Items, i => i.Path.EndsWith("Bookmarks"));
    }

    [Fact]
    public void Specific_rule_claims_a_folder_before_the_generic_one()
    {
        ScanTree tree = Build(@"T:\",
            (@"Users\me\AppData\Roaming\discord\Cache\Cache_Data\a", 30 * MB, Old),
            (@"Users\me\AppData\Roaming\Slack\Cache\Cache_Data\a", 30 * MB, Old));

        List<CleanupFinding> findings = Find(tree);

        Assert.Contains(@"\discord\", Assert.Single(findings, f => f.Rule.Id == "app-discord").Location);
        CleanupFinding generic = Assert.Single(findings, f => f.Rule.Id == "app-electron");
        Assert.Single(generic.Roots);
        Assert.Contains(@"\Slack\", generic.Roots[0]);
    }

    [Fact]
    public void Generic_cache_rule_ignores_folders_that_are_not_chromium_caches()
    {
        ScanTree tree = Build(@"T:\",
            (@"Users\me\AppData\Roaming\TS3Client\cache\badges\a.svg", 30 * MB, Old));

        Assert.DoesNotContain(Find(tree), f => f.Rule.Id == "app-electron");
    }

    [Fact]
    public void Node_modules_only_for_idle_own_projects_and_only_the_topmost()
    {
        ScanTree tree = Build(@"T:\",
            (@"Users\me\code\idle\package.json", 1000, Old),
            (@"Users\me\code\idle\node_modules\react\index.js", 30 * MB, Old),
            (@"Users\me\code\idle\node_modules\react\package.json", 1000, Old),
            (@"Users\me\code\idle\node_modules\react\node_modules\x\y.js", 25 * MB, Old),
            (@"Users\me\code\active\package.json", 1000, Fresh),
            (@"Users\me\code\active\node_modules\vue\index.js", 30 * MB, Old),
            (@"Users\me\code\nopkg\node_modules\a.js", 30 * MB, Old),
            (@"Users\me\AppData\Local\Programs\editor\package.json", 1000, Old),
            (@"Users\me\AppData\Local\Programs\editor\node_modules\a.js", 30 * MB, Old));

        CleanupFinding finding = Assert.Single(Find(tree), f => f.Rule.Id == "dev-node-modules");

        Assert.Equal(@"T:\Users\me\code\idle\node_modules", Assert.Single(finding.Items).Path);
        Assert.Equal(CleanupAction.GanzEntfernen, finding.Rule.Action);
    }

    [Fact]
    public void Steam_library_on_another_drive_is_found_by_name()
    {
        ScanTree tree = Build(@"D:\",
            (@"SteamLibrary\steamapps\shadercache\730\fozpipelinesv6\a.foz", 40 * MB, Old),
            (@"SteamLibrary\steamapps\common\Game\shadercache\keep.bin", 40 * MB, Old));

        CleanupFinding steam = Assert.Single(Find(tree), f => f.Rule.Id == "steam-shadercache");

        Assert.Equal(@"D:\SteamLibrary\steamapps\shadercache\730", Assert.Single(steam.Items).Path);
    }

    [Fact]
    public void Old_installers_in_downloads_are_files_not_folders()
    {
        ScanTree tree = Build(@"T:\",
            (@"Users\me\Downloads\setup_old.exe", 50 * MB, Old),
            (@"Users\me\Downloads\sub\image.iso", 500 * MB, Old),
            (@"Users\me\Downloads\setup_new.exe", 50 * MB, Fresh),
            (@"Users\me\Downloads\photo.jpg", 50 * MB, Old));

        CleanupFinding installers = Assert.Single(Find(tree), f => f.Rule.Id == "old-installers");

        Assert.Equal(new[] { "image.iso", "setup_old.exe" }, installers.Items.Select(i => Path.GetFileName(i.Path)).OrderBy(n => n));
        Assert.All(installers.Items, i => Assert.False(i.IsDirectory));
        Assert.Equal(50 * MB, installers.KeptBytes);
    }

    [Fact]
    public void Hints_have_no_items_and_tiny_findings_are_dropped()
    {
        ScanTree tree = Build(@"T:\",
            (@"hiberfil.sys", 6000 * MB, Old),
            (@"Users\me\AppData\Local\CrashDumps\tiny.dmp", 1000, Old));

        List<CleanupFinding> findings = Find(tree);

        CleanupFinding hint = Assert.Single(findings, f => f.Rule.Id == "hint-hiberfil");
        Assert.Empty(hint.Items);
        Assert.DoesNotContain(findings, f => f.Rule.Id == "error-reports");
    }

    [Fact]
    public void Paths_outside_the_scan_produce_nothing()
    {
        ScanTree tree = Build(@"T:\Users\me\code",
            (@"project\readme.md", 2 * MB, Old));

        Assert.Empty(Find(tree));
    }
}

public sealed class CleanupExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sizeshift-cleanup-" + Guid.NewGuid().ToString("N"));

    public CleanupExecutorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (string link in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                         .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0).ToList())
                Directory.Delete(link, false);
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string relative, DateTime? whenUtc = null)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string('x', 100));
        if (whenUtc is { } when)
        {
            File.SetCreationTimeUtc(path, when);
            File.SetLastWriteTimeUtc(path, when);
        }
        return path;
    }

    private static CleanupRule Rule(FindingSafety safety, int minAgeDays = 0) => new()
    {
        Id = "test", Group = "Test", Title = "Test", Explanation = "Test",
        Safety = safety, Action = CleanupAction.InhaltLeeren, MinAgeDays = minAgeDays
    };

    [Fact]
    public void Deletes_safe_items_permanently_but_keeps_files_newer_than_the_cutoff()
    {
        DateTime old = DateTime.UtcNow.AddDays(-30);
        string cache = Path.Combine(_root, "Cache");
        Write(@"Cache\a.bin", old);
        Write(@"Cache\sub\b.bin", old);
        string young = Write(@"Cache\sub\young.bin");     // gerade eben geschrieben

        var finding = new CleanupFinding
        {
            Rule = Rule(FindingSafety.Sicher, minAgeDays: 2),
            Roots = new[] { cache },
            Items = new[]
            {
                new CleanupItem(Path.Combine(cache, "a.bin"), false, 100, 1),
                new CleanupItem(Path.Combine(cache, "sub"), true, 200, 2)
            }
        };

        CleanupOutcome outcome = CleanupExecutor.Run(new[] { finding }, recycleEverything: false, null, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(cache, "a.bin")));
        Assert.False(File.Exists(Path.Combine(cache, @"sub\b.bin")));
        Assert.True(File.Exists(young));
        Assert.True(Directory.Exists(cache));                 // der Ordner selbst bleibt
        Assert.Equal(2, outcome.DeletedFiles);
        Assert.Equal(1, outcome.KeptYoung);
    }

    [Fact]
    public void Never_follows_a_junction_inside_the_cleaned_folder()
    {
        string cache = Path.Combine(_root, "Cache");
        string precious = Write(@"Elsewhere\precious.txt");
        Write(@"Cache\sub\junk.bin");
        JunctionManager.Create(Path.Combine(cache, @"sub\link"), Path.Combine(_root, "Elsewhere"));

        var finding = new CleanupFinding
        {
            Rule = Rule(FindingSafety.Sicher),
            Roots = new[] { cache },
            Items = new[] { new CleanupItem(Path.Combine(cache, "sub"), true, 100, 1) }
        };

        CleanupExecutor.Run(new[] { finding }, recycleEverything: false, null, CancellationToken.None);

        Assert.True(File.Exists(precious));
        Assert.False(Directory.Exists(Path.Combine(cache, "sub")));
    }

    [Fact]
    public void Refuses_items_outside_the_found_roots()
    {
        string cache = Path.Combine(_root, "Cache");
        Directory.CreateDirectory(cache);
        string outside = Write(@"Other\keep.txt");

        var finding = new CleanupFinding
        {
            Rule = Rule(FindingSafety.Sicher),
            Roots = new[] { cache },
            Items = new[] { new CleanupItem(outside, false, 100, 1) }
        };

        CleanupOutcome outcome = CleanupExecutor.Run(new[] { finding }, recycleEverything: false, null, CancellationToken.None);

        Assert.True(File.Exists(outside));
        Assert.Equal(1, outcome.Skipped);
    }

    [Fact]
    public void Careful_findings_go_to_the_recycle_bin()
    {
        string cache = Path.Combine(_root, "Cache");
        string file = Write(@"Cache\to-recycle.bin");

        var finding = new CleanupFinding
        {
            Rule = Rule(FindingSafety.Vorsichtig),
            Roots = new[] { cache },
            Items = new[] { new CleanupItem(file, false, 100, 1) }
        };

        Assert.True(CleanupExecutor.GoesToRecycleBin(finding, recycleEverything: false));
        CleanupOutcome outcome = CleanupExecutor.Run(new[] { finding }, recycleEverything: false, null, CancellationToken.None);

        Assert.False(File.Exists(file));
        Assert.Equal(1, outcome.RecycledItems);
        Assert.Equal(0, outcome.DeletedFiles);
    }
}
