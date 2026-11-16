using System.Text;

namespace TreeSizeBetter.Core.Model;

/// <summary>
/// Speichert alle Datei- und Ordnernamen als UTF-8 in 4-MiB-Bloecken.
/// Ein string pro Eintrag waere bei 5 Mio. Dateien rund 300 MB plus 5 Mio.
/// Objekt-Header; hier kostet ein Name nur seine Bytes. Namen werden erst
/// beim Anzeigen zu strings dekodiert.
/// NTFS erlaubt maximal 255 Zeichen pro Namen, also hoechstens 765 UTF-8-Bytes:
/// ein Name passt garantiert in einen Block und die Laenge in ein ushort.
/// </summary>
public sealed class NamePool
{
    private const int ChunkShift = 22;              // 4 MiB
    private const int ChunkSize = 1 << ChunkShift;
    private const int ChunkMask = ChunkSize - 1;
    private const int MaxNameBytes = 1024;

    private readonly List<byte[]> _chunks = new();
    private long _next;

    public long ApproximateBytes => (long)_chunks.Count * ChunkSize;

    /// <summary>Legt einen Namen ab. Nicht threadsicher - Aufrufer haelt die Baum-Sperre.</summary>
    public long Add(ReadOnlySpan<char> name, out ushort byteLength)
    {
        int offset = (int)(_next & ChunkMask);
        int chunkIndex = (int)(_next >> ChunkShift);

        // Nie ueber eine Blockgrenze schreiben - sonst muesste das Lesen zwei Bloecke zusammensetzen.
        if (offset + MaxNameBytes > ChunkSize)
        {
            chunkIndex++;
            offset = 0;
            _next = (long)chunkIndex << ChunkShift;
        }

        while (_chunks.Count <= chunkIndex) _chunks.Add(new byte[ChunkSize]);

        int written = Encoding.UTF8.GetBytes(name, _chunks[chunkIndex].AsSpan(offset));
        byteLength = (ushort)written;
        long handle = _next;
        _next += written;
        return handle;
    }

    public ReadOnlySpan<byte> GetBytes(long handle, int length)
        => _chunks[(int)(handle >> ChunkShift)].AsSpan((int)(handle & ChunkMask), length);

    public string GetString(long handle, int length)
        => length == 0 ? string.Empty : Encoding.UTF8.GetString(GetBytes(handle, length));

    public void AppendTo(StringBuilder target, long handle, int length)
    {
        if (length == 0) return;
        Span<char> buffer = stackalloc char[MaxNameBytes];
        int chars = Encoding.UTF8.GetChars(GetBytes(handle, length), buffer);
        target.Append(buffer[..chars]);
    }
}
