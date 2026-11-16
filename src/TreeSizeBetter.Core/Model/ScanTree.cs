using System.Text;

namespace TreeSizeBetter.Core.Model;

/// <summary>
/// Das Ergebnis eines Scans als flacher Baum. Enthaelt keine Logik zum Scannen -
/// Scanner fuellen den Baum, Analyse und UI lesen ihn.
/// </summary>
public sealed class ScanTree
{
    public ChunkedList<FsEntry> Entries { get; } = new();
    public ChunkedList<DirAggregate> Aggregates { get; } = new();
    public NamePool Names { get; } = new();

    /// <summary>Pfad wie vom Benutzer angegeben, z. B. C:\ oder D:\Spiele.</summary>
    public string RootPath { get; private set; } = string.Empty;

    /// <summary>Pfad ohne abschliessenden Trenner, Basis fuer alle zusammengesetzten Pfade.</summary>
    private string _rootPrefix = string.Empty;

    public const int RootIndex = 0;

    /// <summary>Sperre fuer das Anhaengen. Die Enumeration selbst laeuft ausserhalb.</summary>
    internal readonly object Gate = new();

    public int Count => Entries.Count;
    public bool IsEmpty => Entries.Count == 0;

    public long ApproximateBytes =>
        Entries.ApproximateBytes + Aggregates.ApproximateBytes + Names.ApproximateBytes;

    public int CreateRoot(string rootPath, uint attributes, long createdUtc, long modifiedUtc, long accessedUtc)
    {
        if (Entries.Count != 0) throw new InvalidOperationException("Wurzel existiert bereits.");

        RootPath = rootPath;
        _rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (_rootPrefix.Length == 0) _rootPrefix = rootPath;

        long nameRef = Names.Add(_rootPrefix.AsSpan(), out ushort nameLength);
        var entry = new FsEntry
        {
            Parent = -1,
            FirstChild = -1,
            ChildCount = 0,
            AggregateIndex = Aggregates.Add(DirAggregate.Empty),
            NameRef = nameRef,
            NameLength = nameLength,
            Depth = 0,
            Attributes = attributes | FileAttr.Directory,
            CreatedUtc = createdUtc,
            ModifiedUtc = modifiedUtc,
            AccessedUtc = accessedUtc
        };
        return Entries.Add(entry);
    }

    /// <summary>
    /// Uebernimmt den kompletten Inhalt eines Ordners. Gibt den Index des ersten
    /// Kindes zurueck; die Kinder liegen danach zusammenhaengend.
    /// </summary>
    public int AppendChildren(int parentIndex, ReadOnlySpan<RawChild> children, ReadOnlySpan<char> nameBuffer)
    {
        if (children.Length == 0) return -1;

        lock (Gate)
        {
            int start = Entries.Reserve(children.Length);
            ushort depth = (ushort)(Entries[parentIndex].Depth + 1);

            for (int i = 0; i < children.Length; i++)
            {
                RawChild raw = children[i];
                long nameRef = Names.Add(nameBuffer.Slice(raw.NameOffset, raw.NameLength), out ushort nameLength);

                ref FsEntry entry = ref Entries[start + i];
                entry.Parent = parentIndex;
                entry.FirstChild = -1;
                entry.ChildCount = 0;
                entry.AggregateIndex = raw.IsDirectory ? Aggregates.Add(DirAggregate.Empty) : -1;
                entry.NameRef = nameRef;
                entry.NameLength = nameLength;
                entry.Depth = depth;
                entry.Attributes = raw.Attributes;
                entry.ReparseTag = raw.ReparseTag;
                entry.Length = raw.Length;
                entry.SizeOnDisk = raw.SizeOnDisk;
                entry.CreatedUtc = raw.CreatedUtc;
                entry.ModifiedUtc = raw.ModifiedUtc;
                entry.AccessedUtc = raw.AccessedUtc;
            }

            ref FsEntry parent = ref Entries[parentIndex];
            parent.FirstChild = start;
            parent.ChildCount = children.Length;
            return start;
        }
    }

    public string GetName(int index)
    {
        ref FsEntry e = ref Entries[index];
        return Names.GetString(e.NameRef, e.NameLength);
    }

    /// <summary>
    /// Name als UTF-8-Bytes, ohne einen string zu erzeugen. Filter laufen darauf,
    /// weil ein string pro Eintrag bei ueber einer Million Kandidaten genau die
    /// Allokationen verursacht, die den Filter langsam machen.
    /// </summary>
    public ReadOnlySpan<byte> GetNameBytes(int index)
    {
        ref FsEntry e = ref Entries[index];
        return Names.GetBytes(e.NameRef, e.NameLength);
    }

    /// <summary>
    /// Setzt den vollen Pfad aus der Elternkette zusammen.
    /// Nur fuer Anzeige und Aktionen gedacht, nicht fuer den Hot Path.
    /// </summary>
    public string GetFullPath(int index)
    {
        if (index == RootIndex) return RootPath;

        var chain = new List<int>(16);
        int current = index;
        while (current > RootIndex)
        {
            chain.Add(current);
            current = Entries[current].Parent;
        }

        var sb = new StringBuilder(_rootPrefix, _rootPrefix.Length + chain.Count * 16);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            sb.Append(Path.DirectorySeparatorChar);
            ref FsEntry e = ref Entries[chain[i]];
            Names.AppendTo(sb, e.NameRef, e.NameLength);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Sucht einen vollstaendigen Pfad im Baum. -1, wenn er ausserhalb des
    /// Scans liegt oder beim Scan noch nicht existierte.
    /// </summary>
    public int FindPath(string fullPath)
    {
        if (IsEmpty) return -1;
        string path = fullPath.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(path, _rootPrefix, StringComparison.OrdinalIgnoreCase)) return RootIndex;
        if (path.Length <= _rootPrefix.Length + 1 ||
            !path.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            path[_rootPrefix.Length] != Path.DirectorySeparatorChar)
            return -1;

        int current = RootIndex;
        foreach (string part in path[(_rootPrefix.Length + 1)..].Split(Path.DirectorySeparatorChar))
        {
            int found = -1;
            foreach (int child in Children(current))
            {
                if (string.Equals(GetName(child), part, StringComparison.OrdinalIgnoreCase))
                {
                    found = child;
                    break;
                }
            }
            if (found < 0) return -1;
            current = found;
        }
        return current;
    }

    public ref DirAggregate AggregateOf(int index)
    {
        ref FsEntry e = ref Entries[index];
        if (e.AggregateIndex < 0) throw new InvalidOperationException("Dateien haben keine Aggregat-Kennzahlen.");
        return ref Aggregates[e.AggregateIndex];
    }

    /// <summary>Groesse eines Eintrags: bei Ordnern rekursiv, bei Dateien die eigene.</summary>
    public long SizeOf(int index)
    {
        ref FsEntry e = ref Entries[index];
        return e.AggregateIndex >= 0 ? Aggregates[e.AggregateIndex].TotalSize : e.Length;
    }

    public long SizeOnDiskOf(int index)
    {
        ref FsEntry e = ref Entries[index];
        return e.AggregateIndex >= 0 ? Aggregates[e.AggregateIndex].TotalSizeOnDisk : e.SizeOnDisk;
    }

    public IEnumerable<int> Children(int index)
    {
        int first = Entries[index].FirstChild;
        int count = Entries[index].ChildCount;
        for (int i = 0; i < count; i++) yield return first + i;
    }

    /// <summary>
    /// Rechnet alle rekursiven Kennzahlen in einem einzigen Rueckwaertsdurchlauf aus.
    /// Funktioniert ohne Rekursion und ohne Stack, weil beim Aufbau garantiert ist,
    /// dass ein Elternteil einen kleineren Index hat als jedes seiner Kinder:
    /// bei Index i sind alle Nachkommen also schon verarbeitet.
    /// </summary>
    public void Aggregate()
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            ref FsEntry e = ref Entries[i];

            long size, onDisk, newestWrite, oldestWrite, newestAccess, oldestAccess;
            int files, dirs;

            if (e.AggregateIndex >= 0)
            {
                ref DirAggregate own = ref Aggregates[e.AggregateIndex];
                size = own.TotalSize;
                onDisk = own.TotalSizeOnDisk;
                files = own.FileCount;
                dirs = own.DirCount + 1;                 // der Ordner selbst zaehlt beim Elternteil mit
                newestWrite = own.NewestWriteUtc;
                oldestWrite = own.OldestWriteUtc;
                newestAccess = own.NewestAccessUtc;
                oldestAccess = own.OldestAccessUtc;
                // Der Zeitstempel des Ordners selbst wird bewusst NICHT eingerechnet:
                // er aendert sich schon, wenn darin eine Datei angelegt oder umbenannt
                // wird, und wuerde jeden Ordner frisch aussehen lassen.
            }
            else
            {
                size = e.Length;
                onDisk = e.SizeOnDisk;
                files = 1;
                dirs = 0;
                newestWrite = oldestWrite = e.ModifiedUtc;
                newestAccess = oldestAccess = e.AccessedUtc;
            }

            if (e.Parent < 0) continue;

            int parentAgg = Entries[e.Parent].AggregateIndex;
            ref DirAggregate pa = ref Aggregates[parentAgg];
            pa.TotalSize += size;
            pa.TotalSizeOnDisk += onDisk;
            pa.FileCount += files;
            pa.DirCount += dirs;
            if (newestWrite > pa.NewestWriteUtc) pa.NewestWriteUtc = newestWrite;
            if (oldestWrite < pa.OldestWriteUtc) pa.OldestWriteUtc = oldestWrite;
            if (newestAccess > pa.NewestAccessUtc) pa.NewestAccessUtc = newestAccess;
            if (oldestAccess < pa.OldestAccessUtc) pa.OldestAccessUtc = oldestAccess;
        }
    }
}
