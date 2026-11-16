using System.Text;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Cleanup;

/// <summary>Ein einzelnes Ding, das beim Aufräumen entfernt wird.</summary>
public sealed record CleanupItem(string Path, bool IsDirectory, long Bytes, int Files);

/// <summary>
/// Was eine Regel im Scan gefunden hat. Die Zahlen stammen aus dem Scan; was
/// wirklich frei wird, sieht man erst nach dem Aufräumen - gesperrte Dateien
/// bleiben liegen.
/// </summary>
public sealed class CleanupFinding
{
    public required CleanupRule Rule { get; init; }

    /// <summary>Die gefundenen Orte. Gelöscht wird nie etwas außerhalb davon.</summary>
    public required IReadOnlyList<string> Roots { get; init; }

    public required IReadOnlyList<CleanupItem> Items { get; init; }

    /// <summary>Belegter Platz der Elemente, die aufgeräumt würden.</summary>
    public long Bytes { get; init; }

    public int Files { get; init; }

    /// <summary>Platz, der wegen der Altersgrenze liegen bleibt.</summary>
    public long KeptBytes { get; init; }

    public string Location => Rule.DisplayLocation
                              ?? (Roots.Count == 1 ? Roots[0] : Loc.F("cleanup.locations", Roots.Count, Roots[0]));
}

/// <summary>
/// Sucht die Regeln im Scan-Baum. Liest nur den Baum, nicht die Platte -
/// deshalb dauert das auch bei Millionen Einträgen nur einen Augenblick.
/// </summary>
public static class CleanupScanner
{
    public static List<CleanupFinding> Find(
        ScanTree tree, IReadOnlyList<CleanupRule> rules, long nowFileTime, CancellationToken ct = default)
    {
        var findings = new List<CleanupFinding>();
        if (tree.IsEmpty) return findings;

        var claimed = new HashSet<int>();

        foreach (CleanupRule rule in rules)
        {
            ct.ThrowIfCancellationRequested();

            List<int> locations = rule.FolderNameAnywhere is { } name
                ? FindByName(tree, name, ct)
                : rule.Locations.SelectMany(pattern => Resolve(tree, pattern)).Distinct().ToList();

            locations = locations
                .Where(index => !IsClaimed(tree, claimed, index))
                .Where(index => PassesPlaceFilters(tree, rule, index))
                .ToList();
            if (rule.TopmostOnly) locations = TopmostOnly(tree, locations);

            // Zwischenablage, DNS-Cache: keine Dateien. Der Ort (Windows-Ordner) zeigt nur,
            // dass das Systemlaufwerk gescannt wurde - so erscheint der Fund genau einmal.
            if (rule.IsVirtual)
            {
                if (locations.Count > 0)
                    findings.Add(new CleanupFinding
                    {
                        Rule = rule,
                        Roots = new[] { tree.GetFullPath(locations[0]) },
                        Items = Array.Empty<CleanupItem>()
                    });
                continue;
            }

            long cutoff = rule.MinAgeDays > 0 ? nowFileTime - rule.MinAgeDays * TimeSpan.TicksPerDay : long.MaxValue;

            // node_modules: jeder Fund eigenständig, damit man Projekt für Projekt
            // entscheiden kann. Sonst eine Zeile je Regel.
            bool perLocation = rule.FolderNameAnywhere is not null && rule.Action == CleanupAction.GanzEntfernen;
            var groups = perLocation ? locations.Select(l => new List<int> { l }) : new[] { locations };

            foreach (List<int> group in groups)
            {
                if (group.Count == 0) continue;

                var items = new List<CleanupItem>();
                long bytes = 0, kept = 0;
                int files = 0;

                foreach (int location in group)
                    Collect(tree, rule, location, cutoff, items, ref bytes, ref files, ref kept);

                if (rule.Action != CleanupAction.NurHinweis && items.Count == 0) continue;
                if (bytes < rule.MinBytes) continue;

                // Privatsphaere-Regeln picken einzelne Dateien heraus und beanspruchen den Ort nicht.
                if (!rule.IsPrivacy)
                    foreach (int location in group) claimed.Add(location);
                // Bei einzelnen Dateien nur die Orte nennen, an denen wirklich etwas gefunden
                // wurde - "Profile\*" passt sonst auf jeden Unterordner von "User Data".
                List<string> roots = group.Select(tree.GetFullPath).ToList();
                if (rule.Action == CleanupAction.DateienEntfernen)
                    roots = roots.Where(r => items.Any(i => IsUnder(i.Path, r))).ToList();

                findings.Add(new CleanupFinding
                {
                    Rule = rule,
                    Roots = roots,
                    Items = items,
                    Bytes = bytes,
                    Files = files,
                    KeptBytes = kept
                });
            }
        }

        return findings;
    }

    private static void Collect(
        ScanTree tree, CleanupRule rule, int location, long cutoff,
        List<CleanupItem> items, ref long bytes, ref int files, ref long kept)
    {
        ref FsEntry entry = ref tree.Entries[location];

        switch (rule.Action)
        {
            case CleanupAction.NurHinweis:
                bytes += tree.SizeOnDiskOf(location);
                files += FilesOf(tree, location);
                break;

            case CleanupAction.PapierkorbLeeren:
            case CleanupAction.GanzEntfernen:
            {
                int ageIndex = rule.AgeOfParent && entry.Parent >= 0 ? entry.Parent : location;
                if (NewestOf(tree, ageIndex) > cutoff)
                {
                    kept += tree.SizeOnDiskOf(location);
                    break;
                }
                long size = tree.SizeOnDiskOf(location);
                items.Add(new CleanupItem(tree.GetFullPath(location), entry.IsDirectory, size, FilesOf(tree, location)));
                bytes += size;
                files += FilesOf(tree, location);
                break;
            }

            case CleanupAction.InhaltLeeren:
                if (!entry.IsDirectory) break;
                foreach (int child in tree.Children(location))
                {
                    ref FsEntry c = ref tree.Entries[child];
                    long size = tree.SizeOnDiskOf(child);
                    if (c.IsReparsePoint) continue;          // nie durch Verknüpfungen hindurch
                    if (NewestOf(tree, child) > cutoff)
                    {
                        kept += size;
                        continue;
                    }
                    items.Add(new CleanupItem(tree.GetFullPath(child), c.IsDirectory, size, FilesOf(tree, child)));
                    bytes += size;
                    files += FilesOf(tree, child);
                }
                break;

            case CleanupAction.DateienEntfernen:
                if (!entry.IsDirectory || (rule.FileExtensions is null && rule.FileNames is null)) break;
                var stack = new Stack<(int Index, string Relative)>();
                stack.Push((location, string.Empty));
                while (stack.Count > 0)
                {
                    (int current, string prefix) = stack.Pop();
                    foreach (int child in tree.Children(current))
                    {
                        ref FsEntry c = ref tree.Entries[child];
                        if (c.IsReparsePoint) continue;
                        string fileName = tree.GetName(child);
                        string relative = prefix.Length == 0 ? fileName : prefix + "\\" + fileName;
                        if (c.IsDirectory)
                        {
                            stack.Push((child, relative));
                            continue;
                        }
                        string extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
                        bool byExtension = rule.FileExtensions is not null && Array.IndexOf(rule.FileExtensions, extension) >= 0;
                        bool byName = rule.FileNames is not null && rule.FileNames.Any(pattern => Wildcard.IsMatch(relative, pattern));
                        if (!byExtension && !byName) continue;

                        if (NewestOf(tree, child) > cutoff)
                        {
                            kept += c.SizeOnDisk;
                            continue;
                        }
                        items.Add(new CleanupItem(tree.GetFullPath(child), false, c.SizeOnDisk, 1));
                        bytes += c.SizeOnDisk;
                        files++;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Jüngster Zeitpunkt, an dem etwas mit dem Eintrag passiert ist: letzte
    /// Änderung im Teilbaum, aber auch das Anlegen des Eintrags selbst. Das
    /// Anlegen zählt mit, weil Installer Archive mit alten Zeitstempeln nach
    /// Temp entpacken - am Änderungsdatum allein sähe ein gerade laufender
    /// Installer uralt aus.
    /// </summary>
    internal static long NewestOf(ScanTree tree, int index)
    {
        ref FsEntry e = ref tree.Entries[index];
        long newest = Math.Max(e.CreatedUtc, e.ModifiedUtc);
        if (e.AggregateIndex >= 0)
            newest = Math.Max(newest, tree.Aggregates[e.AggregateIndex].NewestWriteUtc);
        return newest;
    }

    private static int FilesOf(ScanTree tree, int index)
    {
        ref FsEntry e = ref tree.Entries[index];
        return e.AggregateIndex >= 0 ? tree.Aggregates[e.AggregateIndex].FileCount : 1;
    }

    /// <summary>Löst einen Pfad mit "*"-Segmenten im Baum auf. Pfade außerhalb des Scans ergeben nichts.</summary>
    internal static List<int> Resolve(ScanTree tree, string pattern)
    {
        var result = new List<int>();
        string root = tree.RootPath.TrimEnd('\\');
        if (!pattern.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return result;

        string[] segments = pattern[(root.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = new List<int> { ScanTree.RootIndex };

        for (int s = 0; s < segments.Length && current.Count > 0; s++)
        {
            string segment = segments[s];
            bool last = s == segments.Length - 1;
            var next = new List<int>();

            foreach (int parent in current)
            {
                foreach (int child in tree.Children(parent))
                {
                    ref FsEntry c = ref tree.Entries[child];
                    if (c.IsReparsePoint) continue;
                    if (!last && !c.IsDirectory) continue;

                    if (segment == "*")
                    {
                        if (c.IsDirectory) next.Add(child);
                    }
                    else if (string.Equals(tree.GetName(child), segment, StringComparison.OrdinalIgnoreCase))
                    {
                        next.Add(child);
                    }
                }
            }
            current = next;
        }

        if (segments.Length == 0) return result;
        result.AddRange(current);
        return result;
    }

    /// <summary>Ordner mit dem Namen "name" oder "eltern\name" irgendwo im Baum.</summary>
    private static List<int> FindByName(ScanTree tree, string spec, CancellationToken ct)
    {
        string[] parts = spec.Split('\\');
        byte[] name = Encoding.UTF8.GetBytes(parts[^1].ToLowerInvariant());
        byte[]? parentName = parts.Length > 1 ? Encoding.UTF8.GetBytes(parts[^2].ToLowerInvariant()) : null;

        var result = new List<int>();
        for (int i = 1; i < tree.Count; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            ref FsEntry e = ref tree.Entries[i];
            if (!e.IsDirectory || e.IsReparsePoint) continue;
            if (!EqualsAsciiIgnoreCase(tree.GetNameBytes(i), name)) continue;
            if (parentName is not null &&
                (e.Parent < 0 || !EqualsAsciiIgnoreCase(tree.GetNameBytes(e.Parent), parentName)))
                continue;
            result.Add(i);
        }
        return result;
    }

    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> value, byte[] lower)
    {
        if (value.Length != lower.Length) return false;
        for (int i = 0; i < value.Length; i++)
        {
            byte b = value[i];
            if (b is >= (byte)'A' and <= (byte)'Z') b += 32;
            if (b != lower[i]) return false;
        }
        return true;
    }

    private static bool IsClaimed(ScanTree tree, HashSet<int> claimed, int index)
    {
        for (int current = index; current >= 0; current = tree.Entries[current].Parent)
            if (claimed.Contains(current)) return true;
        return false;
    }

    private static bool PassesPlaceFilters(ScanTree tree, CleanupRule rule, int index)
    {
        if (rule.OnlyUnder is null && rule.NotUnder is null && rule.RequiredSiblingFile is null) return true;

        string path = tree.GetFullPath(index);
        if (rule.OnlyUnder is { } only && !only.Any(p => IsUnder(path, p))) return false;
        if (rule.NotUnder is { } not && not.Any(p => IsUnder(path, p))) return false;

        if (rule.RequiredSiblingFile is { } sibling)
        {
            int parent = tree.Entries[index].Parent;
            if (parent < 0) return false;
            bool found = tree.Children(parent).Any(c =>
                !tree.Entries[c].IsDirectory &&
                string.Equals(tree.GetName(c), sibling, StringComparison.OrdinalIgnoreCase));
            if (!found) return false;
        }
        return true;
    }

    internal static bool IsUnder(string path, string root)
    {
        root = root.TrimEnd('\\');
        return path.Length > root.Length
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && path[root.Length] == '\\';
    }

    /// <summary>Lässt Treffer weg, die unter einem anderen Treffer liegen.</summary>
    private static List<int> TopmostOnly(ScanTree tree, List<int> locations)
    {
        var set = new HashSet<int>(locations);
        return locations.Where(index =>
        {
            for (int p = tree.Entries[index].Parent; p >= 0; p = tree.Entries[p].Parent)
                if (set.Contains(p)) return false;
            return true;
        }).ToList();
    }
}
