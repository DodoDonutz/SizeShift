using System.Collections;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>
/// Schreibgeschuetzte Liste von Tabellenzeilen ueber ein Array von Baum-Indizes.
///
/// Der Witz daran: die eigentlichen Zeilenobjekte entstehen erst, wenn das
/// DataGrid sie anfordert - bei 200.000 Treffern leben also nur die paar Dutzend
/// sichtbaren Zeilen. Dafuer muss IList (nicht nur IEnumerable) implementiert
/// sein, denn nur dann greift WPF per Index zu statt die ganze Liste
/// durchzulaufen.
/// </summary>
public sealed class RowCollection : IReadOnlyList<EntryRow>, IList
{
    private readonly ScanContext _context;
    private readonly int[] _indices;
    private readonly long _shareBasis;
    private readonly EntryRow?[] _cache;

    public RowCollection(ScanContext context, int[] indices, long shareBasis)
    {
        _context = context;
        _indices = indices;
        _shareBasis = shareBasis;
        _cache = new EntryRow?[indices.Length];
    }

    public static RowCollection Empty(ScanContext context) => new(context, Array.Empty<int>(), 0);

    public int Count => _indices.Length;

    /// <summary>Gesamtgroesse aller Zeilen - fuer die Fusszeile, ohne alle Zeilen zu bauen.</summary>
    public long TotalSize
    {
        get
        {
            long sum = 0;
            for (int i = 0; i < _indices.Length; i++) sum += _context.Tree.SizeOf(_indices[i]);
            return sum;
        }
    }

    public EntryRow this[int index]
    {
        get
        {
            EntryRow? row = _cache[index];
            if (row is not null) return row;
            row = new EntryRow(_context, _indices[index], index, _shareBasis);
            _cache[index] = row;
            return row;
        }
    }

    public IEnumerator<EntryRow> GetEnumerator()
    {
        for (int i = 0; i < _indices.Length; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // --- IList, nur lesend ----------------------------------------------
    // WPF braucht diese Schnittstelle fuer den indizierten Zugriff. Alles,
    // was die Liste veraendern wuerde, ist bewusst nicht unterstuetzt.

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    bool IList.IsFixedSize => true;
    bool IList.IsReadOnly => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => _indices;

    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();

    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

    int IList.IndexOf(object? value)
    {
        // Zeilen kennen ihre eigene Position, deshalb ohne Suche.
        if (value is not EntryRow row) return -1;
        int position = row.RowPosition;
        if ((uint)position >= (uint)_indices.Length) return -1;
        return ReferenceEquals(_cache[position], row) ? position : -1;
    }

    void ICollection.CopyTo(Array array, int index)
    {
        for (int i = 0; i < _indices.Length; i++) array.SetValue(this[i], index + i);
    }
}
