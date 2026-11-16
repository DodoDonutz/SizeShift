using System.Runtime.InteropServices;

namespace TreeSizeBetter.Core.Model;

/// <summary>
/// Ein Eintrag im Dateisystem - Datei oder Ordner. Bewusst ein struct ohne
/// Verweise, damit mehrere Millionen Eintraege in einem flachen Block liegen.
/// Kinder liegen immer zusammenhaengend: FirstChild .. FirstChild+ChildCount-1.
/// Ein Elternteil hat immer einen kleineren Index als seine Kinder - darauf
/// beruht die Aggregation in einem einzigen Rueckwaertsdurchlauf.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FsEntry
{
    public int Parent;          // -1 bei der Wurzel
    public int FirstChild;      // -1 wenn keine Kinder
    public int ChildCount;
    public int AggregateIndex;  // Index in ScanTree.Aggregates, -1 bei Dateien

    public long NameRef;        // Handle in den NamePool
    public ushort NameLength;   // UTF-8-Bytes
    public ushort Depth;

    public uint Attributes;     // FILE_ATTRIBUTE_*
    public uint ReparseTag;     // nur gesetzt bei Reparse-Points

    public long Length;         // logische Groesse
    public long SizeOnDisk;     // belegt (im API-Scan auf Clustergroesse gerundet)

    public long CreatedUtc;     // Windows-FileTime-Ticks
    public long ModifiedUtc;
    public long AccessedUtc;

    public bool IsDirectory => (Attributes & FileAttr.Directory) != 0;
    public bool IsReparsePoint => (Attributes & FileAttr.ReparsePoint) != 0;
    public bool IsHidden => (Attributes & FileAttr.Hidden) != 0;
    public bool IsSystem => (Attributes & FileAttr.System) != 0;
    public bool IsCompressed => (Attributes & FileAttr.Compressed) != 0;
    public bool IsSparse => (Attributes & FileAttr.SparseFile) != 0;
}

/// <summary>Win32-Dateiattribute als Konstanten, um die Enum-Konvertierung im Hot Path zu sparen.</summary>
public static class FileAttr
{
    public const uint ReadOnly = 0x00000001;
    public const uint Hidden = 0x00000002;
    public const uint System = 0x00000004;
    public const uint Directory = 0x00000010;
    public const uint Archive = 0x00000020;
    public const uint Temporary = 0x00000100;
    public const uint SparseFile = 0x00000200;
    public const uint ReparsePoint = 0x00000400;
    public const uint Compressed = 0x00000800;
    public const uint Offline = 0x00001000;
    public const uint NotContentIndexed = 0x00002000;
    public const uint Encrypted = 0x00004000;
    public const uint RecallOnOpen = 0x00040000;
    public const uint RecallOnDataAccess = 0x00400000;
}
