using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace LzFilm.Core;

/// <summary>Input of one "version" of the archive: a file, a buffer, or many files read as one stream.</summary>
internal interface IDataSource : IDisposable
{
    long Length { get; }

    /// <summary>Reads exactly <c>buf.Length</c> bytes at <paramref name="offset"/> or throws.</summary>
    void ReadExact(Span<byte> buf, long offset);
}

internal sealed class FileSource : IDataSource
{
    private readonly SafeFileHandle _h;
    private readonly string _path;

    public FileSource(string path)
    {
        _path = path;
        _h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Length = RandomAccess.GetLength(_h);
    }

    public long Length { get; }

    public void ReadExact(Span<byte> buf, long offset)
    {
        if (SegmentCodec.ReadAt(_h, buf, offset) != buf.Length)
            throw new IOException($"{Path.GetFileName(_path)} became shorter while it was being read (was it modified during the backup?).");
    }

    public void Dispose() => _h.Dispose();
}

internal sealed class MemorySource : IDataSource
{
    private readonly byte[] _data;

    public MemorySource(byte[] data) => _data = data;

    public long Length => _data.Length;

    public void ReadExact(Span<byte> buf, long offset)
    {
        if (offset < 0 || offset + buf.Length > _data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        _data.AsSpan((int)offset, buf.Length).CopyTo(buf);
    }

    public void Dispose() { }
}

/// <summary>
/// The files of a <see cref="FileTable"/> read one after another as a single stream. Reads must be
/// sequential (the archiver reads in order); the SHA-256 of every file is computed on the way.
/// </summary>
internal sealed class ConcatSource : IDataSource
{
    private readonly FolderScan _scan;
    private readonly List<int> _files;     // indices of file entries in data order
    private int _cur = -1;
    private SafeFileHandle? _h;
    private IncrementalHash? _hash;
    private long _next;                    // next expected stream offset
    private int _hashed;                   // number of files whose hash is complete

    public ConcatSource(FolderScan scan)
    {
        _scan = scan;
        _files = Enumerable.Range(0, scan.Table.Entries.Count)
            .Where(i => !scan.Table.Entries[i].IsDirectory)
            .OrderBy(i => scan.Table.Entries[i].Offset)
            .ToList();
        Length = scan.Table.DataLength;
        SkipEmpty();
    }

    public long Length { get; }

    /// <summary>True when every file has been read completely (all hashes are known).</summary>
    public bool AllHashed => _hashed == _files.Count;

    public void ReadExact(Span<byte> buf, long offset)
    {
        if (offset != _next)
            throw new InvalidOperationException("ConcatSource must be read sequentially.");
        while (buf.Length > 0)
        {
            if (_hashed >= _files.Count)
                throw new IOException("Read past the end of the file data.");
            var e = _scan.Table.Entries[_files[_hashed]];
            OpenCurrent();
            long inFile = _next - e.Offset;
            int k = (int)Math.Min(buf.Length, e.Size - inFile);
            int got = SegmentCodec.ReadAt(_h!, buf[..k], inFile);
            if (got != k)
                throw new IOException($"{_scan.SourcePaths[_files[_hashed]]} became shorter while it was being read (was it modified during the backup?).");
            _hash!.AppendData(buf[..k]);
            _next += k;
            buf = buf[k..];
            if (inFile + k == e.Size)
                FinishCurrent();
        }
    }

    /// <summary>Reads the rest of the files only to compute their hashes (used when a backup is resumed after the data was complete).</summary>
    public void HashRemaining(CancellationToken ct)
    {
        var buf = new byte[1 << 20];
        while (_next < Length)
        {
            ct.ThrowIfCancellationRequested();
            int k = (int)Math.Min(buf.Length, Length - _next);
            ReadExact(buf.AsSpan(0, k), _next);
        }
    }

    private void OpenCurrent()
    {
        int idx = _files[_hashed];
        if (_cur == idx)
            return;
        _h?.Dispose();
        _cur = idx;
        try
        {
            _h = File.OpenHandle(_scan.SourcePaths[idx], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Cannot read {_scan.SourcePaths[idx]}: {ex.Message}", ex);
        }
        _hash ??= IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    private void FinishCurrent()
    {
        var e = _scan.Table.Entries[_files[_hashed]];
        e.Sha256 = _hash!.GetHashAndReset();
        _h?.Dispose();
        _h = null;
        _cur = -1;
        _hashed++;
        SkipEmpty();
    }

    private void SkipEmpty()
    {
        while (_hashed < _files.Count && _scan.Table.Entries[_files[_hashed]].Size == 0)
        {
            _scan.Table.Entries[_files[_hashed]].Sha256 = SHA256.HashData(ReadOnlySpan<byte>.Empty);
            _hashed++;
        }
    }

    public void Dispose()
    {
        _h?.Dispose();
        _hash?.Dispose();
    }
}

/// <summary>Everything the core archiver needs, independent of the archive type.</summary>
internal sealed class CoreJob
{
    public List<VersionEntry> Entries { get; init; } = new();
    /// <summary>Opens the input of a version; called once per version, in order.</summary>
    public Func<int, IDataSource> Open { get; init; } = _ => throw new InvalidOperationException();
    /// <summary>Files-and-folders archive: versions are coded independently.</summary>
    public bool Files { get; init; }
    /// <summary>Snapshot chain only: the input paths (the previous version is the reference).</summary>
    public IReadOnlyList<string>? SnapInputs { get; init; }
    public string OutputDirectory { get; init; } = "";
    public string BaseName { get; init; } = "";
    public BackupOptions Options { get; init; } = new();
    public byte[]? SfxStub { get; init; }
    public bool Resume { get; init; }

    public VolumeLayout Layout => new()
    {
        Directory = OutputDirectory,
        BaseName = BaseName,
        SelfExtracting = Options.SelfExtracting,
        VolumeSize = Options.VolumeSize,
    };
}
