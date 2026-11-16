using System.Runtime.InteropServices;

namespace TreeSizeBetter.Core.Model;

/// <summary>
/// Zwischenform, die ein Scanner-Worker fuer einen gefundenen Eintrag fuellt,
/// bevor der ganze Ordnerinhalt in einem Rutsch (und unter einer kurzen Sperre)
/// in den Baum uebernommen wird. Der Name liegt dabei noch im Zeichenpuffer des
/// Workers, nicht als eigener string - das spart bei Millionen Dateien die
/// Allokation und damit einen Grossteil des GC-Drucks.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RawChild
{
    public int NameOffset;
    public int NameLength;
    public uint Attributes;
    public uint ReparseTag;
    public long Length;
    public long SizeOnDisk;
    public long CreatedUtc;
    public long ModifiedUtc;
    public long AccessedUtc;

    public bool IsDirectory => (Attributes & FileAttr.Directory) != 0;
    public bool IsReparsePoint => (Attributes & FileAttr.ReparsePoint) != 0;
}
