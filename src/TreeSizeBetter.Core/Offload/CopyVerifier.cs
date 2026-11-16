using System.IO.Hashing;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Offload;

public sealed record VerifyResult(
    bool Ok,
    long Files,
    long Directories,
    long Bytes,
    long HashedFiles,
    long HashedBytes,
    IReadOnlyList<string> Problems)
{
    public string Summary => Ok
        ? Loc.F("verify.ok", Files, Directories, SizeFormat.Bytes(Bytes), HashedFiles, SizeFormat.Bytes(HashedBytes))
        : Loc.F("verify.problems", Problems.Count) + string.Join("; ", Problems.Take(5));
}

/// <summary>
/// Prueft, ob eine Kopie dem Original entspricht - bevor irgendetwas am
/// Original passiert.
///
/// Stufe 1, immer: jeder Eintrag muss auf beiden Seiten existieren, mit
/// gleicher Art (Datei/Ordner/Verknuepfung), gleicher Groesse und gleichem
/// Aenderungszeitpunkt. Das faengt fehlende Dateien, abgebrochene Kopien und
/// alles, was waehrend des Kopierens veraendert wurde.
///
/// Stufe 2, Stichprobe: Inhalt per XxHash128 vergleichen - alle kleinen Dateien
/// (dort sitzen Konfigurationen und Spielstaende), die groessten, und ein
/// zufaelliger Rest. Auf Wunsch alles.
/// </summary>
public static class CopyVerifier
{
    private const long SmallFileLimit = 1024 * 1024;
    private const int MaxSmallFiles = 3000;
    private const int LargestFiles = 100;
    private const double RandomShare = 0.02;
    private const long SampleByteBudget = 4L * 1024 * 1024 * 1024;
    private const int MaxReportedProblems = 50;

    private readonly record struct Item(bool IsDirectory, bool IsLink, long Length, long ModifiedUtc);

    public static async Task<VerifyResult> VerifyAsync(
        string source, string target, bool fullHash, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report(Loc.T("verify.inventory"));
        Dictionary<string, Item> left = await IndexAsync(source, ct);
        Dictionary<string, Item> right = await IndexAsync(target, ct);

        var problems = new List<string>();
        void Problem(string text)
        {
            if (problems.Count < MaxReportedProblems) problems.Add(text);
        }

        long files = 0, directories = 0, bytes = 0;
        var candidates = new List<(string Path, long Length)>();

        foreach (var (relative, item) in left)
        {
            if (!right.TryGetValue(relative, out Item copy))
            {
                Problem(Loc.F("verify.missing", relative));
                continue;
            }
            if (copy.IsDirectory != item.IsDirectory || copy.IsLink != item.IsLink)
            {
                Problem(Loc.F("verify.otherKind", relative));
                continue;
            }

            if (item.IsDirectory)
            {
                directories++;
                continue;
            }
            if (item.IsLink) continue;

            files++;
            bytes += item.Length;

            if (copy.Length != item.Length)
                Problem(Loc.F("verify.size", item.Length, copy.Length, relative));
            else if (Math.Abs(copy.ModifiedUtc - item.ModifiedUtc) > 2 * TimeSpan.TicksPerSecond)
                Problem(Loc.F("verify.time", relative));
            else
                candidates.Add((relative, item.Length));
        }

        foreach (string relative in right.Keys)
            if (!left.ContainsKey(relative)) Problem(Loc.F("verify.extra", relative));

        if (problems.Count > 0)
            return new VerifyResult(false, files, directories, bytes, 0, 0, problems);

        List<(string Path, long Length)> sample = fullHash ? candidates : PickSample(candidates);
        long hashedBytes = 0;
        int done = 0;

        foreach (var (relative, length) in sample)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 50 == 0)
                progress?.Report(Loc.F("verify.hashing", done, sample.Count));

            UInt128 a = await HashAsync(Path.Combine(source, relative), ct);
            UInt128 b = await HashAsync(Path.Combine(target, relative), ct);
            if (a != b)
            {
                Problem(Loc.F("verify.content", relative));
                if (problems.Count >= 5) break;
            }
            hashedBytes += length;
        }

        return new VerifyResult(problems.Count == 0, files, directories, bytes, sample.Count, hashedBytes, problems);
    }

    /// <summary>Alle kleinen (bis zur Obergrenze), die groessten und ein zufaelliger Rest - mit Bytebudget.</summary>
    private static List<(string Path, long Length)> PickSample(List<(string Path, long Length)> candidates)
    {
        var random = new Random(4711);   // fester Startwert: gleiche Eingabe, gleiche Stichprobe
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, long)>();
        long budget = SampleByteBudget;

        void Take((string Path, long Length) item)
        {
            if (budget - item.Length < 0 || !chosen.Add(item.Path)) return;
            budget -= item.Length;
            result.Add(item);
        }

        var small = candidates.Where(c => c.Length <= SmallFileLimit).ToList();
        if (small.Count > MaxSmallFiles) small = small.OrderBy(_ => random.Next()).Take(MaxSmallFiles).ToList();
        foreach (var item in small) Take(item);

        foreach (var item in candidates.OrderByDescending(c => c.Length).Take(LargestFiles)) Take(item);

        foreach (var item in candidates)
            if (random.NextDouble() < RandomShare) Take(item);

        return result;
    }

    private static async Task<UInt128> HashAsync(string path, CancellationToken ct)
    {
        var hash = new XxHash128();
        await using var stream = new FileStream(PathUtil.ToExtended(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await hash.AppendAsync(stream, ct);
        return hash.GetCurrentHashAsUInt128();
    }

    /// <summary>Relativer Pfad -> Eigenschaften, ueber die eigene Scan-Engine.</summary>
    private static async Task<Dictionary<string, Item>> IndexAsync(string root, CancellationToken ct)
    {
        ScanResult scan = await new ApiScanner().ScanAsync(new ScanOptions { RootPath = root }, null, ct);
        if (scan.Errors.Any(e => e.Kind == ScanErrorKind.AccessDenied))
            throw new IOException(Loc.F("verify.unreadable", root));

        return BuildIndex(scan.Tree);
    }

    /// <summary>Getrennt von IndexAsync, weil ref-Locals in async-Methoden nicht erlaubt sind.</summary>
    private static Dictionary<string, Item> BuildIndex(ScanTree tree)
    {
        string prefix = tree.GetFullPath(ScanTree.RootIndex).TrimEnd('\\') + '\\';
        var map = new Dictionary<string, Item>(tree.Count, StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < tree.Count; i++)
        {
            ref FsEntry e = ref tree.Entries[i];
            string relative = tree.GetFullPath(i)[prefix.Length..];
            map[relative] = new Item(e.IsDirectory, e.IsReparsePoint, e.Length, e.ModifiedUtc);
        }
        return map;
    }
}
