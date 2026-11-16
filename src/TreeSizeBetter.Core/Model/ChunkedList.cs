using System.Runtime.CompilerServices;

namespace TreeSizeBetter.Core.Model;

/// <summary>
/// Wachsende Liste von Structs in Bloecken fester Groesse.
/// Bewusst statt List&lt;T&gt;/Array-Verdopplung: bei mehreren Millionen Eintraegen
/// wuerde eine Verdopplung kurzzeitig die doppelte Menge Speicher belegen und
/// einen 400-MB-Block ueber die Large Object Heap kopieren. Blockweises Wachsen
/// vermeidet beides und erlaubt eine ref-Indexierung ohne Kopie des Structs.
/// </summary>
public sealed class ChunkedList<T> where T : struct
{
    public const int ChunkShift = 16;
    public const int ChunkSize = 1 << ChunkShift;   // 65536 Eintraege pro Block
    private const int ChunkMask = ChunkSize - 1;

    private T[][] _chunks = new T[4][];
    private int _chunkCount;
    private int _count;

    public int Count => _count;

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return ref _chunks[index >> ChunkShift][index & ChunkMask];
        }
    }

    /// <summary>Haengt einen Eintrag an und gibt seinen Index zurueck.</summary>
    public int Add(in T item)
    {
        int index = _count;
        EnsureCapacityFor(index);
        _chunks[index >> ChunkShift][index & ChunkMask] = item;
        _count = index + 1;
        return index;
    }

    /// <summary>
    /// Reserviert <paramref name="count"/> aufeinanderfolgende Plaetze und gibt den
    /// ersten Index zurueck. Grundlage dafuer, dass die Kinder eines Ordners immer
    /// zusammenhaengend liegen (FirstChild + ChildCount statt Geschwister-Zeigern).
    /// </summary>
    public int Reserve(int count)
    {
        int start = _count;
        for (int i = 0; i < count; i++) EnsureCapacityFor(start + i);
        _count = start + count;
        return start;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacityFor(int index)
    {
        int chunk = index >> ChunkShift;
        if (chunk < _chunkCount) return;
        while (_chunkCount <= chunk)
        {
            if (_chunkCount == _chunks.Length)
            {
                var grown = new T[_chunks.Length * 2][];
                Array.Copy(_chunks, grown, _chunkCount);
                _chunks = grown;
            }
            _chunks[_chunkCount++] = new T[ChunkSize];
        }
    }

    /// <summary>Geschaetzter Speicherverbrauch in Bytes.</summary>
    public long ApproximateBytes => (long)_chunkCount * ChunkSize * Unsafe.SizeOf<T>();
}
