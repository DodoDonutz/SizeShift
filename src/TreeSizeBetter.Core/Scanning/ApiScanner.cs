using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Scanning;

/// <summary>
/// Standard-Scanner ueber die Windows-Suchfunktionen. Braucht keine Admin-Rechte
/// und laeuft auf jedem Dateisystem.
///
/// Zwei Dinge machen ihn deutlich schneller als Directory.EnumerateFileSystemEntries:
/// FindFirstFileEx liefert Groesse und alle drei Zeitstempel direkt mit (kein
/// zweiter Aufruf pro Datei), und die Ordner werden parallel abgearbeitet.
/// </summary>
public sealed class ApiScanner : IScanner
{
    private const int MaxDepth = 512;

    public string Name => Loc.T("scan.standard");

    public bool CanScan(string rootPath, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            reason = Loc.T("scan.noPath");
            return false;
        }
        if (!Directory.Exists(rootPath))
        {
            reason = Loc.F("scan.notFound", rootPath);
            return false;
        }
        reason = null;
        return true;
    }

    public Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => new ScanRun(options, progress, ct).Execute(), CancellationToken.None);

    private readonly record struct DirJob(int Index, string Path, int Depth);

    /// <summary>Pro Worker ein Puffersatz, damit im Hot Path nichts allokiert wird.</summary>
    private sealed class WorkerBuffers
    {
        public char[] Names = new char[64 * 1024];
        public byte[]? DirectoryInfo;   // nur fuer den Leseweg mit Sicherungsrecht
        public int NameUsed;
        public readonly List<RawChild> Children = new(256);

        public void Reset()
        {
            NameUsed = 0;
            Children.Clear();
        }

        public int AddName(ReadOnlySpan<char> name)
        {
            if (NameUsed + name.Length > Names.Length)
                Array.Resize(ref Names, Math.Max(Names.Length * 2, NameUsed + name.Length));

            int offset = NameUsed;
            name.CopyTo(Names.AsSpan(offset));
            NameUsed += name.Length;
            return offset;
        }
    }

    private sealed class ScanRun
    {
        private readonly ScanOptions _options;
        private readonly IProgress<ScanProgress>? _progress;
        private readonly CancellationToken _ct;
        private readonly ScanTree _tree = new();
        private readonly ConcurrentStack<DirJob> _stack = new();
        private readonly ConcurrentBag<ScanError> _errors = new();
        private readonly string[] _excluded;

        private int _remaining;
        private long _files;
        private long _dirs;
        private long _bytes;
        private int _denied;
        private int _rescued;
        private int _cancelled;
        private string _currentPath = string.Empty;
        private uint _bytesPerCluster = 4096;

        public ScanRun(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct)
        {
            _options = options;
            _progress = progress;
            _ct = ct;
            _excluded = options.ExcludedPaths.ToArray();
        }

        public ScanResult Execute()
        {
            var startedUtc = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();

            string root = Path.TrimEndingDirectorySeparator(_options.RootPath);
            if (root.Length == 2 && root[1] == ':') root += Path.DirectorySeparatorChar;

            // Mit Admin-Rechten das Sicherungsrecht einschalten - erst dann sind
            // auch Ordner lesbar, die selbst Administratoren aussperren.
            Elevation.TryEnableBackupPrivilege();

            ReadVolumeInfo(root, out long volumeTotal, out long volumeFree);
            CreateRootEntry(root);

            _remaining = 1;
            _stack.Push(new DirJob(ScanTree.RootIndex, root, 0));

            int threadCount = _options.EffectiveThreadCount;
            var done = new CountdownEvent(threadCount);
            var threads = new Thread[threadCount];
            for (int i = 0; i < threadCount; i++)
            {
                threads[i] = new Thread(() => WorkerLoop(done))
                {
                    IsBackground = true,
                    Name = $"sizeshift-scan-{i}"
                };
                threads[i].Start();
            }

            while (!done.Wait(200))
                _progress?.Report(Snapshot(sw.Elapsed));

            foreach (var t in threads) t.Join();
            done.Dispose();

            _tree.Aggregate();
            sw.Stop();
            _progress?.Report(Snapshot(sw.Elapsed));

            ref DirAggregate rootAgg = ref _tree.AggregateOf(ScanTree.RootIndex);
            return new ScanResult
            {
                Tree = _tree,
                ScannerName = Loc.T("scan.standard"),
                StartedUtc = startedUtc,
                Duration = sw.Elapsed,
                FileCount = rootAgg.FileCount,
                DirectoryCount = rootAgg.DirCount,
                TotalBytes = rootAgg.TotalSize,
                TotalBytesOnDisk = rootAgg.TotalSizeOnDisk,
                BytesPerCluster = _bytesPerCluster,
                SizeOnDiskIsEstimate = true,
                VolumeTotalBytes = volumeTotal,
                VolumeFreeBytes = volumeFree,
                Errors = _errors.ToArray(),
                DeniedCount = _denied,
                RescuedCount = _rescued,
                ScannedElevated = Elevation.BackupPrivilegeEnabled,
                WasCancelled = Volatile.Read(ref _cancelled) != 0
            };
        }

        private void ReadVolumeInfo(string root, out long total, out long free)
        {
            total = 0;
            free = 0;
            string? volumeRoot = PathUtil.GetVolumeRoot(root);
            if (volumeRoot is null) return;

            if (NativeMethods.GetDiskFreeSpace(volumeRoot, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _)
                && sectorsPerCluster > 0 && bytesPerSector > 0)
            {
                _bytesPerCluster = sectorsPerCluster * bytesPerSector;
            }

            try
            {
                var drive = new DriveInfo(volumeRoot);
                total = drive.TotalSize;
                free = drive.AvailableFreeSpace;
            }
            catch (Exception)
            {
                // Netzlaufwerke und nicht bereite Geraete: Volumenangaben bleiben leer,
                // der Scan selbst funktioniert trotzdem.
            }
        }

        private void CreateRootEntry(string root)
        {
            uint attributes = FileAttr.Directory;
            long created = 0, modified = 0, accessed = 0;
            try
            {
                var info = new DirectoryInfo(root);
                attributes = (uint)info.Attributes | FileAttr.Directory;
                created = info.CreationTimeUtc.ToFileTimeUtc();
                modified = info.LastWriteTimeUtc.ToFileTimeUtc();
                accessed = info.LastAccessTimeUtc.ToFileTimeUtc();
            }
            catch (Exception)
            {
                // Laufwerkswurzeln liefern gelegentlich keine brauchbaren Zeitstempel.
            }
            _tree.CreateRoot(root, attributes, created, modified, accessed);
        }

        private void WorkerLoop(CountdownEvent done)
        {
            var buffers = new WorkerBuffers();
            int misses = 0;
            try
            {
                while (true)
                {
                    if (_ct.IsCancellationRequested)
                    {
                        Interlocked.Exchange(ref _cancelled, 1);
                        return;
                    }

                    if (_stack.TryPop(out DirJob job))
                    {
                        misses = 0;
                        try
                        {
                            ProcessDirectory(job, buffers);
                        }
                        catch (Exception ex)
                        {
                            _errors.Add(new ScanError(job.Path, ScanErrorKind.Other, 0, ex.Message));
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _remaining);
                        }
                        continue;
                    }

                    // Erst aussteigen, wenn garantiert kein Ordner mehr in Arbeit ist.
                    // _remaining wird erhoeht, bevor ein Kind auf den Stapel geht, und
                    // erst nach dem Bearbeiten gesenkt - ein Wert von 0 heisst also
                    // wirklich "nichts mehr offen".
                    if (Volatile.Read(ref _remaining) == 0) return;

                    if (++misses < 64) Thread.SpinWait(80);
                    else Thread.Sleep(1);
                }
            }
            finally
            {
                done.Signal();
            }
        }

        private unsafe void ProcessDirectory(in DirJob job, WorkerBuffers buffers)
        {
            Volatile.Write(ref _currentPath, job.Path);

            buffers.Reset();

            Win32FindData data;
            IntPtr handle = NativeMethods.FindFirstFileEx(
                PathUtil.ToSearchPattern(job.Path),
                NativeMethods.FindExInfoBasic,
                &data,
                NativeMethods.FindExSearchNameMatch,
                IntPtr.Zero,
                NativeMethods.FindFirstExLargeFetch);

            if (handle == NativeMethods.InvalidHandleValue)
            {
                int error = Marshal.GetLastWin32Error();

                // Mit Admin-Rechten und Sicherungsrecht: Ordner, deren Rechte selbst
                // Administratoren aussperren (WindowsApps, System Volume Information),
                // ueber den Leseweg fuer Sicherungsprogramme doch noch lesen.
                bool rescued = error == NativeMethods.ErrorAccessDenied
                               && Elevation.BackupPrivilegeEnabled
                               && CollectWithBackupSemantics(job.Path, buffers);
                if (!rescued)
                {
                    RecordWin32Error(job.Path, error);
                    return;
                }
                Interlocked.Increment(ref _rescued);
            }
            else
            {
                try
                {
                    do
                    {
                        int nameLength = 0;
                        while (nameLength < 260 && data.cFileName[nameLength] != '\0') nameLength++;
                        if (nameLength == 0) continue;

                        // "." und ".." ueberspringen, sonst laeuft die Rekursion im Kreis.
                        if (data.cFileName[0] == '.' &&
                            (nameLength == 1 || (nameLength == 2 && data.cFileName[1] == '.')))
                            continue;

                        uint attributes = data.dwFileAttributes;
                        bool isDir = (attributes & FileAttr.Directory) != 0;
                        uint reparseTag = (attributes & FileAttr.ReparsePoint) != 0 ? data.dwReserved0 : 0u;
                        long length = isDir ? 0 : data.FileSize;

                        buffers.Children.Add(new RawChild
                        {
                            NameOffset = buffers.AddName(new ReadOnlySpan<char>(data.cFileName, nameLength)),
                            NameLength = nameLength,
                            Attributes = attributes,
                            ReparseTag = reparseTag,
                            Length = length,
                            SizeOnDisk = EstimateSizeOnDisk(length, attributes, reparseTag, isDir),
                            CreatedUtc = data.CreationTime,
                            ModifiedUtc = data.LastWriteTime,
                            AccessedUtc = data.LastAccessTime
                        });
                    }
                    while (NativeMethods.FindNextFile(handle, &data));

                    int lastError = Marshal.GetLastWin32Error();
                    if (lastError != NativeMethods.ErrorNoMoreFiles && lastError != 0)
                        RecordWin32Error(job.Path, lastError);
                }
                finally
                {
                    NativeMethods.FindClose(handle);
                }
            }

            if (buffers.Children.Count == 0) return;

            var children = CollectionsMarshal.AsSpan(buffers.Children);
            int start = _tree.AppendChildren(job.Index, children, buffers.Names.AsSpan(0, buffers.NameUsed));

            long localBytes = 0;
            int localFiles = 0, localDirs = 0;
            foreach (RawChild child in children)
            {
                if (child.IsDirectory) localDirs++;
                else
                {
                    localFiles++;
                    localBytes += child.Length;
                }
            }
            Interlocked.Add(ref _files, localFiles);
            Interlocked.Add(ref _dirs, localDirs);
            Interlocked.Add(ref _bytes, localBytes);

            if (job.Depth >= MaxDepth)
            {
                _errors.Add(new ScanError(job.Path, ScanErrorKind.Other, 0,
                    Loc.F("scan.maxDepth", MaxDepth)));
                return;
            }

            for (int i = 0; i < children.Length; i++)
            {
                RawChild child = children[i];
                if (!child.IsDirectory) continue;

                // Junctions und Symlinks werden nicht verfolgt - sonst Doppelzaehlung
                // und bei zirkulaeren Links eine Endlosschleife. Cloud-Ordner (OneDrive,
                // iCloud) sind dagegen echte lokale Ordner mit Markierung, keine Links:
                // die werden betreten, sonst fehlen die Dateien, die wirklich lokal liegen.
                if (child.IsReparsePoint && !_options.FollowReparsePoints
                    && !ReparseTags.IsCloudPlaceholder(child.ReparseTag)) continue;

                string childPath = PathUtil.Combine(
                    job.Path,
                    new string(buffers.Names, child.NameOffset, child.NameLength));

                if (IsExcluded(childPath)) continue;

                Interlocked.Increment(ref _remaining);
                _stack.Push(new DirJob(start + i, childPath, job.Depth + 1));
            }
        }

        /// <summary>
        /// Zweiter Leseweg fuer Ordner, deren Rechte das normale Auflisten verbieten.
        /// Oeffnet den Ordner mit FILE_FLAG_BACKUP_SEMANTICS - bei aktivem
        /// Sicherungsrecht (SeBackupPrivilege) umgeht das die Rechtepruefung fuer
        /// Lesezugriffe, so wie es Sicherungsprogramme tun - und listet ihn ueber
        /// GetFileInformationByHandleEx(FileFullDirectoryInfo). Nebenbei liefert
        /// dieser Weg den echten belegten Platz (AllocationSize) statt einer Schaetzung.
        /// </summary>
        private unsafe bool CollectWithBackupSemantics(string path, WorkerBuffers buffers)
        {
            IntPtr handle = NativeMethods.CreateFile(
                PathUtil.ToExtended(path),
                NativeMethods.FileListDirectory,
                NativeMethods.FileShareAll,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle == NativeMethods.InvalidHandleValue) return false;

            try
            {
                buffers.DirectoryInfo ??= new byte[64 * 1024];
                fixed (byte* buffer = buffers.DirectoryInfo)
                {
                    int infoClass = NativeMethods.FileFullDirectoryRestartInfo;
                    while (NativeMethods.GetFileInformationByHandleEx(handle, infoClass, buffer, buffers.DirectoryInfo.Length))
                    {
                        infoClass = NativeMethods.FileFullDirectoryInfo;
                        int offset = 0;
                        while (true)
                        {
                            // Aufbau von FILE_FULL_DIR_INFO, siehe Windows-SDK:
                            // 0 NextEntryOffset, 8 Erstellt, 16 Zugriff, 24 Geaendert,
                            // 40 Dateilaenge, 48 belegter Platz, 56 Attribute,
                            // 60 Namenslaenge in Bytes, 64 EaSize (bei Reparse-Points: der Tag), 68 Name.
                            byte* entry = buffer + offset;
                            int nameBytes = *(int*)(entry + 60);
                            var name = new ReadOnlySpan<char>(entry + 68, nameBytes / 2);

                            if (!(name is "." or ".."))
                            {
                                uint attributes = *(uint*)(entry + 56);
                                bool isDir = (attributes & FileAttr.Directory) != 0;
                                uint reparseTag = (attributes & FileAttr.ReparsePoint) != 0 ? *(uint*)(entry + 64) : 0u;
                                long length = isDir ? 0 : *(long*)(entry + 40);
                                long allocated = isDir ? 0 : *(long*)(entry + 48);

                                buffers.Children.Add(new RawChild
                                {
                                    NameOffset = buffers.AddName(name),
                                    NameLength = name.Length,
                                    Attributes = attributes,
                                    ReparseTag = reparseTag,
                                    Length = length,
                                    SizeOnDisk = allocated,
                                    CreatedUtc = *(long*)(entry + 8),
                                    AccessedUtc = *(long*)(entry + 16),
                                    ModifiedUtc = *(long*)(entry + 24)
                                });
                            }

                            int next = *(int*)entry;
                            if (next == 0) break;
                            offset += next;
                        }
                    }
                }
                // Ende der Liste meldet die Funktion als ERROR_NO_MORE_FILES - das ist Erfolg.
                return Marshal.GetLastWin32Error() == NativeMethods.ErrorNoMoreFiles;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        /// <summary>
        /// Belegter Platz, auf Clustergroesse aufgerundet. Fuer komprimierte und
        /// sparse Dateien zu hoch, fuer Cloud-Platzhalter wird 0 angesetzt, weil
        /// sie lokal keinen Platz brauchen. Der MFT-Scan liefert spaeter die
        /// echten Werte.
        /// </summary>
        private long EstimateSizeOnDisk(long length, uint attributes, uint reparseTag, bool isDirectory)
        {
            if (isDirectory) return 0;

            const uint cloudish = FileAttr.Offline | FileAttr.RecallOnOpen | FileAttr.RecallOnDataAccess;
            if ((attributes & cloudish) != 0 || ReparseTags.IsCloudPlaceholder(reparseTag)) return 0;
            if (length == 0) return 0;

            uint cluster = _bytesPerCluster;
            return (length + cluster - 1) / cluster * cluster;
        }

        private bool IsExcluded(string path)
        {
            for (int i = 0; i < _excluded.Length; i++)
                if (PathUtil.IsSameOrBelow(path, _excluded[i])) return true;
            return false;
        }

        private void RecordWin32Error(string path, int error)
        {
            switch (error)
            {
                case NativeMethods.ErrorAccessDenied:
                    Interlocked.Increment(ref _denied);
                    _errors.Add(new ScanError(path, ScanErrorKind.AccessDenied, error, Loc.T("scan.accessDenied")));
                    break;
                case NativeMethods.ErrorFileNotFound:
                case NativeMethods.ErrorPathNotFound:
                    // Waehrend eines Scans verschwinden laufend temporaere Dateien.
                    // Das ist normal und wird nicht als Fehler gemeldet.
                    break;
                case NativeMethods.ErrorNotReady:
                    _errors.Add(new ScanError(path, ScanErrorKind.DeviceNotReady, error, Loc.T("scan.notReady")));
                    break;
                default:
                    _errors.Add(new ScanError(path, ScanErrorKind.Other, error,
                        new System.ComponentModel.Win32Exception(error).Message));
                    break;
            }
        }

        private ScanProgress Snapshot(TimeSpan elapsed) => new(
            Entries: Volatile.Read(ref _files) + Volatile.Read(ref _dirs),
            Files: Volatile.Read(ref _files),
            Directories: Volatile.Read(ref _dirs),
            Bytes: Volatile.Read(ref _bytes),
            DirectoriesPending: Volatile.Read(ref _remaining),
            Denied: Volatile.Read(ref _denied),
            CurrentPath: Volatile.Read(ref _currentPath),
            Elapsed: elapsed);
    }
}
