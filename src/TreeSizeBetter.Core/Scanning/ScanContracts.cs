using TreeSizeBetter.Core.Model;

namespace TreeSizeBetter.Core.Scanning;

public sealed class ScanOptions
{
    public required string RootPath { get; init; }

    /// <summary>Anzahl paralleler Worker. 0 = automatisch.</summary>
    public int MaxThreads { get; init; }

    /// <summary>
    /// Reparse-Points (Junctions, Symlinks, OneDrive-Platzhalter) verfolgen.
    /// Standard aus - sonst zaehlt die App nach der ersten Umlagerung die Daten
    /// von D: unter C: mit und laeuft bei zirkulaeren Links endlos.
    /// </summary>
    public bool FollowReparsePoints { get; init; }

    /// <summary>Diese Pfade und alles darunter werden uebersprungen.</summary>
    public IReadOnlyCollection<string> ExcludedPaths { get; init; } = Array.Empty<string>();

    public int EffectiveThreadCount =>
        MaxThreads > 0 ? MaxThreads : Math.Clamp(Environment.ProcessorCount, 2, 16);
}

public sealed record ScanProgress(
    long Entries,
    long Files,
    long Directories,
    long Bytes,
    long DirectoriesPending,
    int Denied,
    string CurrentPath,
    TimeSpan Elapsed)
{
    public double EntriesPerSecond => Elapsed.TotalSeconds > 0.01 ? Entries / Elapsed.TotalSeconds : 0;
}

public enum ScanErrorKind
{
    AccessDenied,
    NotFound,
    DeviceNotReady,
    Other
}

public sealed record ScanError(string Path, ScanErrorKind Kind, int Win32Error, string Message);

public sealed class ScanResult
{
    public required ScanTree Tree { get; init; }
    public required string ScannerName { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required TimeSpan Duration { get; init; }

    public long FileCount { get; init; }
    public long DirectoryCount { get; init; }
    public long TotalBytes { get; init; }
    public long TotalBytesOnDisk { get; init; }

    public uint BytesPerCluster { get; init; }

    /// <summary>
    /// true, wenn "Belegt" nur auf Clustergroesse gerundet ist statt aus dem
    /// Dateisystem gelesen. Gilt fuer den API-Scan; komprimierte und sparse
    /// Dateien sind dort zu gross angesetzt.
    /// </summary>
    public bool SizeOnDiskIsEstimate { get; init; }

    public long VolumeTotalBytes { get; init; }
    public long VolumeFreeBytes { get; init; }

    public IReadOnlyList<ScanError> Errors { get; init; } = Array.Empty<ScanError>();
    public int DeniedCount { get; init; }

    /// <summary>Ordner, die nur dank Sicherungsrecht (Admin-Modus) gelesen werden konnten.</summary>
    public int RescuedCount { get; init; }

    /// <summary>true, wenn mit Admin-Rechten und aktivem Sicherungsrecht gescannt wurde.</summary>
    public bool ScannedElevated { get; init; }
    public bool WasCancelled { get; init; }
}

public interface IScanner
{
    string Name { get; }

    /// <summary>Prueft, ob dieser Scanner den Pfad ueberhaupt bedienen kann (Dateisystem, Rechte).</summary>
    bool CanScan(string rootPath, out string? reason);

    Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct);
}
