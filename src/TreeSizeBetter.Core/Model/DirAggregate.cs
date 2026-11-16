using System.Runtime.InteropServices;

namespace TreeSizeBetter.Core.Model;

/// <summary>
/// Rekursive Kennzahlen eines Ordners. Nur Ordner bekommen so einen Eintrag
/// (typisch 5-10 % aller Eintraege), deshalb liegt das in einem eigenen Block
/// und blaeht FsEntry nicht auf.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DirAggregate
{
    public long TotalSize;        // Summe aller Dateien im Teilbaum
    public long TotalSizeOnDisk;
    public int FileCount;         // Dateien im Teilbaum
    public int DirCount;          // Unterordner im Teilbaum

    public long NewestWriteUtc;   // jüngste Änderung im Teilbaum
    public long OldestWriteUtc;
    public long NewestAccessUtc;  // jüngster Zugriff im Teilbaum - Basis für "Kälte"
    public long OldestAccessUtc;

    public static DirAggregate Empty => new()
    {
        NewestWriteUtc = 0,
        OldestWriteUtc = long.MaxValue,
        NewestAccessUtc = 0,
        OldestAccessUtc = long.MaxValue
    };

    /// <summary>true, wenn der Ordner (rekursiv) keine einzige Datei enthält.</summary>
    public bool IsEmptyOfFiles => FileCount == 0;
}
