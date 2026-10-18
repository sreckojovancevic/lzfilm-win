using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace LzFilm.Core;

/// <summary>
/// Where the parts of an archive live. The logical stream is split into volumes of
/// <see cref="VolumeSize"/> bytes (0 = one file). Volume 1 may be a self-extracting .exe:
/// [stub padded to 4 KiB][volume 1 payload][16-byte trailer: u64 payload offset, "LZSNSFX1"].
/// </summary>
public sealed class VolumeLayout
{
    public string Directory { get; init; } = "";
    public string BaseName { get; init; } = "";
    public bool SelfExtracting { get; init; }
    public long VolumeSize { get; init; }

    public bool Split => VolumeSize > 0;

    public string PathOf(int k)
    {
        if (k == 0)
        {
            if (SelfExtracting)
                return Path.Combine(Directory, BaseName + ".exe");
            return Path.Combine(Directory, Split ? BaseName + ".lzs.001" : BaseName + ".lzs");
        }
        return Path.Combine(Directory, $"{BaseName}.lzs.{k + 1:D3}");
    }

    public int VolumeOf(long logicalOffset) => Split ? (int)(logicalOffset / VolumeSize) : 0;

    public static long AlignStub(long length) =>
        (length + FormatConst.SfxAlignment - 1) / FormatConst.SfxAlignment * FormatConst.SfxAlignment;
}

public class VolumeException : IOException
{
    public int VolumeNumber { get; }
    public string VolumePath { get; }

    public VolumeException(int volumeNumber, string path, string message) : base(message)
    {
        VolumeNumber = volumeNumber;
        VolumePath = path;
    }
}

public sealed class IncompleteArchiveException : IOException
{
    public IncompleteArchiveException(string message) : base(message) { }
}

/// <summary>Writes the logical stream across volumes, forward only, with header patching.</summary>
internal sealed class VolumeWriter : IDisposable
{
    private readonly VolumeLayout _layout;
    private readonly long _prefix;
    private FileStream? _cur;
    private int _curIndex = -1;
    private long _curWritten;

    public long Position { get; private set; }
    public int VolumeCount => _curIndex + 1;
    public long Prefix => _prefix;

    private VolumeWriter(VolumeLayout layout, long prefix)
    {
        _layout = layout;
        _prefix = prefix;
    }

    /// <summary>Starts a new archive. Stale volumes of a previous archive with the same name are removed.</summary>
    public static VolumeWriter Create(VolumeLayout layout, byte[]? stub)
    {
        System.IO.Directory.CreateDirectory(layout.Directory);
        long prefix = layout.SelfExtracting ? VolumeLayout.AlignStub(stub?.Length ?? throw new ArgumentException("A self-extracting archive needs the stub.")) : 0;
        var w = new VolumeWriter(layout, prefix);
        DeleteFrom(layout, 1);
        w._cur = new FileStream(layout.PathOf(0), FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        if (layout.SelfExtracting)
        {
            w._cur.Write(stub);
            w._cur.Write(new byte[prefix - stub!.Length]);
        }
        w._curIndex = 0;
        return w;
    }

    /// <summary>Re-opens an interrupted archive and truncates it to <paramref name="logicalLength"/>.</summary>
    public static VolumeWriter OpenForResume(VolumeLayout layout, long prefix, long logicalLength)
    {
        var w = new VolumeWriter(layout, prefix);
        int k = layout.Split ? (int)((logicalLength - 1) / layout.VolumeSize) : 0;
        long inVol = logicalLength - (layout.Split ? k * layout.VolumeSize : 0);
        DeleteFrom(layout, k + 1);
        w._cur = new FileStream(layout.PathOf(k), FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        w._cur.SetLength((k == 0 ? prefix : 0) + inVol);
        w._cur.Seek(0, SeekOrigin.End);
        w._curIndex = k;
        w._curWritten = inVol;
        w.Position = logicalLength;
        return w;
    }

    private static void DeleteFrom(VolumeLayout layout, int first)
    {
        for (int k = Math.Max(first, 1); ; k++)
        {
            var p = layout.PathOf(k);
            if (!File.Exists(p))
                break;
            File.Delete(p);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (_layout.Split && _curWritten == _layout.VolumeSize)
            {
                _cur!.Flush(true);
                _cur.Dispose();
                _curIndex++;
                _cur = new FileStream(_layout.PathOf(_curIndex), FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
                _curWritten = 0;
            }
            int n = data.Length;
            if (_layout.Split)
                n = (int)Math.Min(n, _layout.VolumeSize - _curWritten);
            _cur!.Write(data[..n]);
            _curWritten += n;
            Position += n;
            data = data[n..];
        }
    }

    /// <summary>Overwrites bytes inside volume 1 (used for the header).</summary>
    public void PatchVolume1(long logicalOffset, ReadOnlySpan<byte> data)
    {
        if (_curIndex == 0)
        {
            long save = _cur!.Position;
            _cur.Seek(_prefix + logicalOffset, SeekOrigin.Begin);
            _cur.Write(data);
            _cur.Seek(save, SeekOrigin.Begin);
            return;
        }
        using var fs = new FileStream(_layout.PathOf(0), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        fs.Seek(_prefix + logicalOffset, SeekOrigin.Begin);
        fs.Write(data);
        fs.Flush(true);
    }

    public void Flush(bool toDisk) => _cur?.Flush(toDisk);

    /// <summary>Appends the self-extractor trailer to volume 1.</summary>
    public void WriteSfxTrailer()
    {
        Span<byte> t = stackalloc byte[FormatConst.SfxTrailerSize];
        BinaryPrimitives.WriteInt64LittleEndian(t, _prefix);
        FormatConst.SfxMagic.CopyTo(t[8..]);
        if (_curIndex == 0)
        {
            _cur!.Seek(0, SeekOrigin.End);
            _cur.Write(t);
            _cur.Flush(true);
            return;
        }
        using var fs = new FileStream(_layout.PathOf(0), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        fs.Seek(0, SeekOrigin.End);
        fs.Write(t);
        fs.Flush(true);
    }

    public void Dispose()
    {
        _cur?.Dispose();
        _cur = null;
    }
}

/// <summary>Random-access reader of the logical stream over all volumes.</summary>
public sealed class ArchiveReader : IDisposable
{
    private readonly SafeFileHandle?[] _handles;
    private readonly long[] _volLength;

    public VolumeLayout Layout { get; }
    public long Prefix { get; }
    public ArchiveHeader Header { get; }
    public Footer? Footer { get; private set; }
    public int VolumeCount => _volLength.Length;
    /// <summary>Logical bytes available (for an incomplete archive: what is on disk).</summary>
    public long AvailableLength { get; }

    private static readonly Regex VolumeName = new(@"^(?<base>.+)\.lzs\.(?<num>\d{3,})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private ArchiveReader(VolumeLayout layout, long prefix, ArchiveHeader header, long[] volLength)
    {
        Layout = layout;
        Prefix = prefix;
        Header = header;
        _volLength = volLength;
        _handles = new SafeFileHandle?[volLength.Length];
        AvailableLength = volLength.Sum();
    }

    /// <summary>
    /// Opens an archive from any of its files (.lzs, .exe, .lzs.001, .lzs.002, ...).
    /// With <paramref name="allowIncomplete"/> an unfinished archive can be opened (for resume).
    /// </summary>
    public static ArchiveReader Open(string anyPath, bool allowIncomplete = false)
    {
        string vol1 = FindVolume1(Path.GetFullPath(anyPath));
        string dir = Path.GetDirectoryName(vol1)!;
        long fileLen = new FileInfo(vol1).Length;
        long prefix = 0, vol1Payload = fileLen;
        bool sfx = false;
        var head = new byte[FormatConst.HeaderSize];

        using (var h = File.OpenHandle(vol1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Span<byte> t = stackalloc byte[FormatConst.SfxTrailerSize];
            if (fileLen >= FormatConst.SfxTrailerSize + FormatConst.HeaderSize &&
                RandomAccess.Read(h, t, fileLen - FormatConst.SfxTrailerSize) == t.Length &&
                t[8..].SequenceEqual(FormatConst.SfxMagic))
            {
                prefix = BinaryPrimitives.ReadInt64LittleEndian(t);
                vol1Payload = fileLen - FormatConst.SfxTrailerSize - prefix;
                sfx = true;
            }
            else if (fileLen >= 2 && IsPeFile(h))
            {
                // Self-extracting archive that was not finished: find the header after the stub.
                prefix = FindHeaderAfterStub(h, fileLen);
                if (prefix < 0)
                    throw new InvalidDataException("This program does not contain an LzFilm archive.");
                vol1Payload = fileLen - prefix;
                sfx = true;
            }
            if (vol1Payload < FormatConst.HeaderSize || ReadFull(h, head, prefix) != head.Length)
                throw new InvalidDataException("File is too short to be an LzFilm archive.");
        }

        var header = ArchiveHeader.Parse(head);
        var layout = new VolumeLayout { Directory = dir, BaseName = header.BaseName, SelfExtracting = sfx, VolumeSize = header.VolumeSize };
        if (!header.Complete && !allowIncomplete)
            throw new IncompleteArchiveException("The archive was not finished (the backup was interrupted). Resume it from the Backup tab.");

        var lengths = new List<long> { vol1Payload };
        if (header.Complete)
        {
            int count = Math.Max(1, header.VolumeCount);
            for (int k = 1; k < count; k++)
            {
                long expected = layout.Split ? Math.Min(layout.VolumeSize, header.LogicalLength - k * layout.VolumeSize) : 0;
                lengths.Add(expected);
            }
            lengths[0] = layout.Split ? Math.Min(layout.VolumeSize, header.LogicalLength) : header.LogicalLength;
        }
        else if (layout.Split)
        {
            for (int k = 1; File.Exists(layout.PathOf(k)); k++)
                lengths.Add(new FileInfo(layout.PathOf(k)).Length);
        }

        var r = new ArchiveReader(layout, prefix, header, lengths.ToArray());
        if (header.Complete)
        {
            long footLen = header.LogicalLength - header.FooterOffset;
            if (header.FooterOffset < FormatConst.HeaderSize || footLen <= 0 || footLen > int.MaxValue)
                throw new InvalidDataException("Archive header points to an invalid footer.");
            var fb = new byte[footLen];
            r.Read(header.FooterOffset, fb);
            r.Footer = Footer.Parse(fb);
        }
        return r;
    }

    private static bool IsPeFile(SafeFileHandle h)
    {
        Span<byte> mz = stackalloc byte[2];
        return RandomAccess.Read(h, mz, 0) == 2 && mz[0] == (byte)'M' && mz[1] == (byte)'Z';
    }

    private static long FindHeaderAfterStub(SafeFileHandle h, long fileLen)
    {
        var buf = new byte[FormatConst.HeaderSize];
        Span<byte> magic = stackalloc byte[4];
        for (long off = FormatConst.SfxAlignment; off + FormatConst.HeaderSize <= fileLen && off < 512L << 20; off += FormatConst.SfxAlignment)
        {
            if (RandomAccess.Read(h, magic, off) != 4 || !magic.SequenceEqual(FormatConst.HeaderMagic))
                continue;
            if (ReadFull(h, buf, off) == buf.Length && ArchiveHeader.LooksLikeHeader(buf))
                return off;
        }
        return -1;
    }

    private static string FindVolume1(string path)
    {
        var name = Path.GetFileName(path);
        var dir = Path.GetDirectoryName(path)!;
        var m = VolumeName.Match(name);
        if (m.Success && m.Groups["num"].Value.TrimStart('0') != "1")
        {
            string b = m.Groups["base"].Value;
            var lzs = Path.Combine(dir, b + ".lzs.001");
            if (File.Exists(lzs))
                return lzs;
            var exe = Path.Combine(dir, b + ".exe");
            if (File.Exists(exe))
                return exe;
            throw new VolumeException(1, lzs, $"The first part of the archive is missing ({b}.lzs.001 or {b}.exe).");
        }
        if (!File.Exists(path))
            throw new FileNotFoundException("Archive not found.", path);
        return path;
    }

    private static int ReadFull(SafeFileHandle h, Span<byte> buf, long offset)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int k = RandomAccess.Read(h, buf[got..], offset + got);
            if (k <= 0)
                break;
            got += k;
        }
        return got;
    }

    private SafeFileHandle Handle(int k)
    {
        var h = _handles[k];
        if (h != null)
            return h;
        var p = Layout.PathOf(k);
        if (!File.Exists(p))
            throw new VolumeException(k + 1, p, $"Volume {k + 1} is missing: {Path.GetFileName(p)}");
        long len = new FileInfo(p).Length - (k == 0 ? Prefix + (Layout.SelfExtracting && Header.Complete ? FormatConst.SfxTrailerSize : 0) : 0);
        if (len < _volLength[k])
            throw new VolumeException(k + 1, p, $"Volume {k + 1} is shorter than expected: {Path.GetFileName(p)}");
        h = File.OpenHandle(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _handles[k] = h;
        return h;
    }

    /// <summary>Reads exactly buf.Length bytes at a logical offset.</summary>
    public void Read(long logicalOffset, Span<byte> buf)
    {
        while (buf.Length > 0)
        {
            int k = Layout.VolumeOf(logicalOffset);
            if (k >= _volLength.Length)
                throw new EndOfStreamException("Read past the end of the archive.");
            long volStart = Layout.Split ? k * Layout.VolumeSize : 0;
            long inVol = logicalOffset - volStart;
            int n = (int)Math.Min(buf.Length, _volLength[k] - inVol);
            if (n <= 0)
                throw new EndOfStreamException("Read past the end of the archive.");
            var h = Handle(k);
            if (ReadFull(h, buf[..n], (k == 0 ? Prefix : 0) + inVol) != n)
                throw new VolumeException(k + 1, Layout.PathOf(k), $"Volume {k + 1} is shorter than expected.");
            logicalOffset += n;
            buf = buf[n..];
        }
    }

    /// <summary>Checks that every volume exists and has the expected size.</summary>
    public List<VolumeProblem> CheckVolumes()
    {
        var list = new List<VolumeProblem>();
        for (int k = 0; k < _volLength.Length; k++)
        {
            var p = Layout.PathOf(k);
            if (!File.Exists(p))
            {
                list.Add(new VolumeProblem(k + 1, p, "missing"));
                continue;
            }
            long len = new FileInfo(p).Length - (k == 0 ? Prefix + (Layout.SelfExtracting && Header.Complete ? FormatConst.SfxTrailerSize : 0) : 0);
            if (len != _volLength[k])
                list.Add(new VolumeProblem(k + 1, p, len < _volLength[k] ? "too short" : "too long"));
        }
        if (Header.Complete && Layout.Split)
        {
            var extra = Layout.PathOf(_volLength.Length);
            if (File.Exists(extra))
                list.Add(new VolumeProblem(_volLength.Length + 1, extra, "unexpected extra volume (from another archive?)"));
        }
        return list;
    }

    public IEnumerable<string> VolumePaths() => Enumerable.Range(0, _volLength.Length).Select(Layout.PathOf);

    public void Dispose()
    {
        foreach (var h in _handles)
            h?.Dispose();
    }
}

public sealed record VolumeProblem(int VolumeNumber, string Path, string Problem);
