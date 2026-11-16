using System.Collections.Concurrent;
using System.IO;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>Belegter Platz und Dateizahl eines Ordners.</summary>
public readonly record struct FolderSize(long BytesOnDisk, long Files, bool Complete);

/// <summary>
/// Rechnet Ordnergroessen fuer die Explorer-Ansicht im Hintergrund aus.
///
/// Gibt es einen frischen Scan, der den Ordner enthaelt, kommt die Zahl sofort
/// aus dem Scan. Sonst wird der Ordner mit demselben Scanner vermessen wie bei
/// der Analyse - hoechstens zwei gleichzeitig, damit die Platte nicht
/// zwischen zehn Ordnern hin- und herspringt. Ergebnisse werden gemerkt und
/// nach einer Umlagerung gezielt verworfen.
/// </summary>
public sealed class FolderSizeService
{
    private readonly SemaphoreSlim _gate = new(2);
    private readonly ConcurrentDictionary<string, Task<FolderSize>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private ScanTree? _scan;

    /// <summary>Der letzte Analyse-Scan. Wird nach Umlagerungen verworfen - danach stimmt er nicht mehr.</summary>
    public void UseScan(ScanTree? tree) => _scan = tree;

    public Task<FolderSize> MeasureAsync(string path)
    {
        if (_scan is { } tree && tree.FindPath(path) is var index and >= 0)
        {
            ref FsEntry entry = ref tree.Entries[index];
            if (entry.AggregateIndex >= 0)
                return Task.FromResult(new FolderSize(tree.SizeOnDiskOf(index), tree.AggregateOf(index).FileCount, true));
        }

        return _cache.GetOrAdd(path, p => Task.Run(() => MeasureLiveAsync(p)));
    }

    /// <summary>Vergisst alle gemerkten Groessen und den Scan - fuer "Aktualisieren".</summary>
    public void Clear()
    {
        _scan = null;
        _cache.Clear();
    }

    /// <summary>Vergisst alles unter und ueber diesem Pfad - beides hat sich geaendert.</summary>
    public void Invalidate(string path)
    {
        _scan = null;
        string trimmed = path.TrimEnd('\\');
        foreach (string key in _cache.Keys)
        {
            string k = key.TrimEnd('\\');
            bool below = k.StartsWith(trimmed + "\\", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase);
            bool above = trimmed.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase);
            if (below || above) _cache.TryRemove(key, out _);
        }
    }

    private async Task<FolderSize> MeasureLiveAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            if (!Directory.Exists(path)) return new FolderSize(0, 0, false);
            ScanResult result = await new ApiScanner().ScanAsync(new ScanOptions { RootPath = path }, null, CancellationToken.None);
            return new FolderSize(result.TotalBytesOnDisk, result.FileCount, result.DeniedCount == 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new FolderSize(0, 0, false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
