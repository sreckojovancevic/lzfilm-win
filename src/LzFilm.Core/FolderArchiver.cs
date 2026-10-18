using System.Diagnostics;
using System.Security.Cryptography;

namespace LzFilm.Core;

/// <summary>
/// Ordinary archives of files and folders ("add files, press pack"). Uses the same container as the
/// snapshot chain — segments, volumes, self-extraction, resume, SHA-256 — with two independent
/// versions: version 0 is the content of all files as one solid stream (similar files next to each
/// other), version 1 is the <see cref="FileTable"/>.
/// </summary>
public static class FolderArchiver
{
    internal const string DataName = "files.data";
    internal const string IndexName = "files.index";

    // ---------------------------------------------------------------------------------------- scan

    /// <summary>
    /// Lists the files and folders to pack. Hidden and system files are included; folder links
    /// (junctions, symbolic links) are stored as empty folders and not followed. Files of the
    /// archive being created (<paramref name="outputDirectory"/>/<paramref name="baseName"/>.*) are left out.
    /// </summary>
    public static FolderScan Scan(IReadOnlyList<string> inputs, string? outputDirectory = null, string? baseName = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (inputs.Count == 0)
            throw new ArgumentException("Add at least one file or folder.");
        string? exclude = outputDirectory != null && baseName != null
            ? Path.GetFullPath(Path.Combine(outputDirectory, baseName)) + "."
            : null;
        var dirs = new List<(FileTableEntry E, string Src)>();
        var files = new List<(FileTableEntry E, string Src)>();
        var skipped = new List<string>();
        var topNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var last = Stopwatch.StartNew();

        string TopName(string full)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
            if (string.IsNullOrEmpty(name))
                name = new string(full.Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } s ? s : "root";
            var n = name;
            for (int k = 2; !topNames.Add(n); k++)
                n = $"{name} ({k})";
            return n;
        }

        bool Excluded(string full) => exclude != null && full.StartsWith(exclude, StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(Path.GetDirectoryName(full), Path.GetDirectoryName(exclude[..^1]), StringComparison.OrdinalIgnoreCase);

        // Size and times from a directory listing can be stale on NTFS (the parent's index entry is
        // updated lazily), so every item is re-read from the item itself.
        void AddFile(FileInfo fi, string arcPath)
        {
            if (Excluded(fi.FullName))
                return;
            fi.Refresh();
            files.Add((new FileTableEntry
            {
                Path = arcPath,
                Size = fi.Length,
                LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                Attributes = fi.Attributes & KeptAttributes,
            }, fi.FullName));
        }

        void Walk(DirectoryInfo dir, string arcPath)
        {
            ct.ThrowIfCancellationRequested();
            dir.Refresh();
            dirs.Add((new FileTableEntry
            {
                Path = arcPath,
                IsDirectory = true,
                LastWriteUtcTicks = dir.LastWriteTimeUtc.Ticks,
                Attributes = dir.Attributes & KeptAttributes,
            }, dir.FullName));
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                skipped.Add($"{dir.FullName}: folder link, not followed");
                return;
            }
            IEnumerable<FileSystemInfo> items;
            try
            {
                items = dir.EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                }).ToList();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                skipped.Add($"{dir.FullName}: {e.Message}");
                return;
            }
            foreach (var it in items.OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                var child = arcPath + "/" + it.Name;
                if (it is DirectoryInfo d)
                    Walk(d, child);
                else if (it is FileInfo f)
                    AddFile(f, child);
            }
            if (progress != null && last.ElapsedMilliseconds > 200)
            {
                last.Restart();
                progress.Report(new ProgressInfo { Phase = Phase.Scanning, Done = files.Count, Message = null });
            }
        }

        foreach (var input in inputs)
        {
            var full = Path.GetFullPath(input);
            if (Directory.Exists(full))
                Walk(new DirectoryInfo(full), TopName(full));
            else if (File.Exists(full))
                AddFile(new FileInfo(full), TopName(full));
            else
                throw new FileNotFoundException("Not found.", full);
        }

        // Similar files next to each other compress better: order the data by extension, then name.
        files = files
            .OrderBy(f => System.IO.Path.GetExtension(f.E.Name).ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(f => f.E.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(f => f.E.Path, StringComparer.Ordinal)
            .ToList();
        long off = 0;
        foreach (var (e, _) in files)
        {
            e.Offset = off;
            off += e.Size;
        }
        var scan = new FolderScan { Skipped = skipped };
        scan.Table.DataLength = off;
        foreach (var (e, src) in dirs.OrderBy(d => d.E.Path, StringComparer.Ordinal).Concat(files))
        {
            scan.Table.Entries.Add(e);
            scan.SourcePaths.Add(src);
        }
        return scan;
    }

    private const FileAttributes KeptAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;

    // -------------------------------------------------------------------------------------- create

    private static CoreJob Core(BackupJob job, FolderScan scan)
    {
        long fp = scan.Table.Fingerprint();
        int tableLength = scan.Table.Serialize().Length;
        ConcatSource? data = null;
        return new CoreJob
        {
            Files = true,
            Entries = new List<VersionEntry>
            {
                new() { Name = DataName, Size = scan.Table.DataLength, LastWriteUtcTicks = fp },
                new() { Name = IndexName, Size = tableLength, LastWriteUtcTicks = fp },
            },
            Open = v =>
            {
                if (v == 0)
                    return data = new ConcatSource(scan);
                if (data is not { AllHashed: true })
                {
                    // Resumed after the file data was already complete: the hashes still have to be computed.
                    using var again = new ConcatSource(scan);
                    again.HashRemaining(CancellationToken.None);
                }
                return new MemorySource(scan.Table.Serialize());
            },
            OutputDirectory = job.OutputDirectory,
            BaseName = job.BaseName,
            Options = job.Options,
            SfxStub = job.SfxStub,
            Resume = job.Resume,
        };
    }

    private static void Validate(BackupJob job, FolderScan scan)
    {
        var o = job.Options;
        if (scan.Table.Entries.Count == 0)
            throw new ArgumentException("Nothing to pack.");
        if (string.IsNullOrWhiteSpace(job.BaseName) || job.BaseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid archive name.");
        if (o.Preset is < 0 or > 9)
            throw new ArgumentException("Preset must be 0–9.");
        if (o.Threads is < 1 or > 64)
            throw new ArgumentException("Threads must be 1–64.");
        if (o.SegmentSizeMiB is < 1 or > 1024)
            throw new ArgumentException("Segment size must be 1–1024 MiB.");
        if (o.VolumeSize != 0 && o.VolumeSize < (1L << 20))
            throw new ArgumentException("Volume size must be at least 1 MiB.");
        if (o.SelfExtracting && (job.SfxStub == null || job.SfxStub.Length == 0))
            throw new ArgumentException("The self-extractor is not available in this build.");
    }

    /// <summary>Checks whether an unfinished archive of the same files with the same settings can be continued.</summary>
    public static ResumeInfo InspectResume(BackupJob job, FolderScan scan) => SnapArchiver.InspectResume(Core(job, scan));

    /// <summary>
    /// Packs the scanned files. <see cref="BackupJob.Inputs"/> is not used (the scan is). Returns with
    /// Completed=false if cancelled; the archive can then be resumed with the same files and settings.
    /// </summary>
    public static BackupResult Create(BackupJob job, FolderScan scan, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        Validate(job, scan);
        return SnapArchiver.CreateCore(Core(job, scan), progress, ct);
    }

    // ------------------------------------------------------------------------------------- reading

    private static ArchiveReader OpenFiles(string archivePath)
    {
        var r = ArchiveReader.Open(archivePath);
        if (!r.Header.Files || r.Header.Versions.Count != 2)
        {
            r.Dispose();
            throw new InvalidDataException("This is not a files-and-folders archive.");
        }
        return r;
    }

    private static FileTable ReadTable(ArchiveReader r, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var sha = SnapArchiver.DecodeSegments(r, 1, null, 0, null, (_, d) => ms.Write(d), _ => { }, ct);
        if (!sha!.AsSpan().SequenceEqual(r.Footer!.Versions[1].Sha256))
            throw new InvalidDataException("The file table of the archive is damaged (SHA-256 mismatch).");
        var t = FileTable.Parse(ms.ToArray());
        if (t.DataLength != r.Header.Versions[0].Size)
            throw new InvalidDataException("The file table does not match the archive.");
        return t;
    }

    /// <summary>Reads the list of files and folders in an archive.</summary>
    public static FileTable ReadIndex(string archivePath, CancellationToken ct = default)
    {
        using var r = OpenFiles(archivePath);
        return ReadTable(r, ct);
    }

    /// <summary>
    /// Extracts files into a folder. <paramref name="selection"/> holds archive paths of files or
    /// folders (a folder selects everything below it); null extracts everything. Only the segments
    /// that contain selected files are decoded. Every file is verified with SHA-256.
    /// Existing files are overwritten.
    /// </summary>
    public static RestoreResult Extract(string archivePath, string destinationDirectory, IReadOnlyCollection<string>? selection = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (!Xz.IsAvailable)
            throw new DllNotFoundException("liblzma was not found (liblzma.dll must be next to the program).");
        var sw = Stopwatch.StartNew();
        using var r = OpenFiles(archivePath);
        var table = ReadTable(r, ct);
        Directory.CreateDirectory(destinationDirectory);
        string root = Path.GetFullPath(destinationDirectory);

        var sel = selection?.Select(x => x.TrimEnd('/')).ToHashSet(StringComparer.Ordinal);
        bool Selected(FileTableEntry e)
        {
            if (sel == null)
                return true;
            for (var p = e.Path; ; p = p[..p.LastIndexOf('/')])
            {
                if (sel.Contains(p))
                    return true;
                if (p.LastIndexOf('/') < 0)
                    return false;
            }
        }

        var dirs = table.Directories.Where(Selected).ToList();
        var files = table.Files.Where(Selected).OrderBy(e => e.Offset).ToList();
        if (dirs.Count == 0 && files.Count == 0)
            throw new ArgumentException("Nothing selected.");

        string Target(FileTableEntry e)
        {
            var parts = e.Path.Split('/');
            foreach (var p in parts)
                if (p.Length == 0 || p == "." || p == "..")
                    throw new InvalidDataException($"Unsafe path in the archive: {e.Path}");
            var clean = parts.Select(p =>
            {
                foreach (var c in Path.GetInvalidFileNameChars())
                    p = p.Replace(c, '_');
                return p.TrimEnd(' ', '.') is { Length: > 0 } t ? t : "_";
            });
            var full = Path.GetFullPath(Path.Combine(new[] { root }.Concat(clean).ToArray()));
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe path in the archive: {e.Path}");
            return full;
        }

        foreach (var d in dirs)
            Directory.CreateDirectory(Target(d));

        int seg = r.Header.SegmentSize;
        var want = new HashSet<long>();
        foreach (var f in files.Where(f => f.Size > 0))
            for (long s = f.Offset / seg; s <= (f.Offset + f.Size - 1) / seg; s++)
                want.Add(s);
        long total = files.Sum(f => f.Size);
        long done = 0;
        var last = Stopwatch.StartNew();
        var result = new RestoreResult();

        int next = 0;                       // first selected file not finished yet
        FileStream? outS = null;
        IncrementalHash? hash = null;
        FileTableEntry? open = null;
        string? openPath = null;

        void Finish(FileTableEntry e, string path)
        {
            byte[] got;
            if (e.Size == 0)
            {
                PrepareTarget(path);
                File.WriteAllBytes(path, Array.Empty<byte>());
                got = SHA256.HashData(ReadOnlySpan<byte>.Empty);
            }
            else
            {
                outS!.Dispose();
                outS = null;
                got = hash!.GetHashAndReset();
                open = null;
            }
            if (!got.AsSpan().SequenceEqual(e.Sha256))
            {
                File.Delete(path);
                throw new InvalidDataException($"{e.Path} failed verification (SHA-256 mismatch).");
            }
            ApplyMetadata(path, e, isDir: false);
            result.Files.Add(path);
        }

        void FinishEmptyBefore(long offset)
        {
            while (next < files.Count && files[next].Size == 0 && files[next].Offset <= offset)
            {
                Finish(files[next], Target(files[next]));
                next++;
            }
        }

        try
        {
            if (want.Count > 0)
            {
                SnapArchiver.DecodeSegments(r, 0, null, 0, s => want.Contains(s), (a, data) =>
                {
                    long b = a + data.Length;
                    while (next < files.Count && files[next].Offset < b)
                    {
                        var e = files[next];
                        if (e.Size == 0)
                        {
                            FinishEmptyBefore(e.Offset);
                            continue;
                        }
                        long end = e.Offset + e.Size;
                        if (open != e)
                        {
                            openPath = Target(e);
                            PrepareTarget(openPath);
                            outS = new FileStream(openPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                            hash ??= IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            open = e;
                        }
                        long s0 = Math.Max(a, e.Offset), t0 = Math.Min(b, end);
                        var piece = data.Slice((int)(s0 - a), (int)(t0 - s0));
                        outS!.Write(piece);
                        hash!.AppendData(piece);
                        if (end > b)
                            break;              // continues in the next segment
                        Finish(e, openPath!);
                        next++;
                    }
                }, k =>
                {
                    done += k;
                    if (progress != null && last.ElapsedMilliseconds > 200)
                    {
                        last.Restart();
                        progress.Report(new ProgressInfo { Phase = Phase.Restoring, Done = Math.Min(done, total), Total = total });
                    }
                }, ct);
            }
            FinishEmptyBefore(long.MaxValue);
            if (next != files.Count)
                throw new InvalidDataException("Not all selected files could be extracted (the archive index is inconsistent).");
        }
        finally
        {
            if (outS != null)
            {
                outS.Dispose();
                try { File.Delete(openPath!); } catch (IOException) { }
            }
            hash?.Dispose();
        }
        // Folder times last (writing files changes them), deepest first.
        foreach (var d in dirs.OrderByDescending(d => d.Path.Count(c => c == '/')))
            ApplyMetadata(Target(d), d, isDir: true);
        progress?.Report(new ProgressInfo { Phase = Phase.Done, Done = total, Total = total });
        return new RestoreResult { Files = result.Files, Elapsed = sw.Elapsed };
    }

    private static void PrepareTarget(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var a = File.GetAttributes(path);
            if ((a & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    private static void ApplyMetadata(string path, FileTableEntry e, bool isDir)
    {
        try
        {
            var t = new DateTime(e.LastWriteUtcTicks, DateTimeKind.Utc);
            if (isDir)
            {
                Directory.SetLastWriteTimeUtc(path, t);
                if (OperatingSystem.IsWindows() && (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    File.SetAttributes(path, (File.GetAttributes(path) | (e.Attributes & (FileAttributes.Hidden | FileAttributes.System))));
            }
            else
            {
                File.SetLastWriteTimeUtc(path, t);
                if (e.Attributes != 0)
                    File.SetAttributes(path, e.Attributes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    // -------------------------------------------------------------------------------------- verify

    /// <summary>
    /// Rebuilds all file data from the archive and compares it with the stored hashes and with the
    /// current files on disk. Returns the problems found (empty = OK).
    /// </summary>
    public static List<string> Verify(string archivePath, FolderScan scan, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var problems = new List<string>();
        using var r = OpenFiles(archivePath);
        var table = ReadTable(r, ct);
        if (table.Entries.Count != scan.Table.Entries.Count ||
            !table.Entries.Zip(scan.Table.Entries).All(p => p.First.Path == p.Second.Path && p.First.Size == p.Second.Size && p.First.Offset == p.Second.Offset))
        {
            problems.Add("The file list in the archive does not match the files on disk.");
            return problems;
        }
        var byOffset = Enumerable.Range(0, table.Entries.Count).Where(i => !table.Entries[i].IsDirectory && table.Entries[i].Size > 0)
            .OrderBy(i => table.Entries[i].Offset).ToList();
        var reported = new HashSet<int>();
        long total = table.DataLength, done = 0;
        var last = Stopwatch.StartNew();
        using var src = new ConcatSource(new FolderScan { Table = CloneForHashing(table), SourcePaths = scan.SourcePaths });
        var buf = new byte[r.Header.SegmentSize];
        string? readError = null;
        var tracker = new FileHashTracker(table);
        var sha = SnapArchiver.DecodeSegments(r, 0, null, 0, null, (a, data) =>
        {
            tracker.Feed(a, data);
            if (readError != null)
                return;
            try
            {
                src.ReadExact(buf.AsSpan(0, data.Length), a);
            }
            catch (IOException e)
            {
                readError = e.Message;
                return;
            }
            int same = data.CommonPrefixLength(buf.AsSpan(0, data.Length));
            while (same < data.Length)
            {
                long at = a + same;
                int idx = byOffset.FindLast(i => table.Entries[i].Offset <= at);
                if (idx >= 0 && reported.Add(idx))
                    problems.Add($"{table.Entries[idx].Path}: differs from the current file (changed after packing?).");
                if (idx < 0)
                    break;
                long next = table.Entries[idx].Offset + table.Entries[idx].Size - a;
                if (next >= data.Length)
                    break;
                int k = (int)next;
                same = k + data[k..].CommonPrefixLength(buf.AsSpan(k, data.Length - k));
            }
        }, k =>
        {
            done += k;
            if (progress != null && last.ElapsedMilliseconds > 200)
            {
                last.Restart();
                progress.Report(new ProgressInfo { Phase = Phase.Verifying, Done = done, Total = total });
            }
        }, ct);
        if (!sha!.AsSpan().SequenceEqual(r.Footer!.Versions[0].Sha256))
            problems.Insert(0, "The archive does not reproduce the packed data (SHA-256 mismatch).");
        if (readError != null)
            problems.Add(readError);
        foreach (var bad in tracker.Mismatches)
            problems.Add($"{bad}: the stored checksum does not match the packed data.");
        return problems;
    }

    private static FileTable CloneForHashing(FileTable t)
    {
        var c = new FileTable { DataLength = t.DataLength };
        foreach (var e in t.Entries)
            c.Entries.Add(new FileTableEntry { Path = e.Path, IsDirectory = e.IsDirectory, Size = e.Size, Offset = e.Offset, LastWriteUtcTicks = e.LastWriteUtcTicks, Attributes = e.Attributes });
        return c;
    }

    /// <summary>Computes per-file SHA-256 from the data stream as it is decoded in order.</summary>
    private sealed class FileHashTracker
    {
        private readonly List<FileTableEntry> _files;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int _next;
        public List<string> Mismatches { get; } = new();

        public FileHashTracker(FileTable t) => _files = t.Files.Where(f => f.Size > 0).OrderBy(f => f.Offset).ToList();

        public void Feed(long a, ReadOnlySpan<byte> data)
        {
            long b = a + data.Length;
            while (_next < _files.Count && _files[_next].Offset < b)
            {
                var e = _files[_next];
                long s0 = Math.Max(a, e.Offset), t0 = Math.Min(b, e.Offset + e.Size);
                _hash.AppendData(data.Slice((int)(s0 - a), (int)(t0 - s0)));
                if (e.Offset + e.Size > b)
                    return;
                if (!_hash.GetHashAndReset().AsSpan().SequenceEqual(e.Sha256))
                    Mismatches.Add(e.Path);
                _next++;
            }
        }
    }
}
