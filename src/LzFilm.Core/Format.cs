using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LzFilm.Core;

// ---------------------------------------------------------------------------------------------
// .lzs format, version 3 (all integers little-endian). The "logical stream" is the archive as one
// byte sequence; with volumes it is split across files, see VolumeLayout.
//
//   [header, 64 KiB]   "LZSN" u16 ver=3 u16 flags ...                    (patched when finished)
//                      flags: 1 = motion, 2 = complete, 4 = files-and-folders archive
//   [records]          SEG1 (segment of a version) ... VEND (end of version) ... per version
//   [footer]           FOOT ... FEND sha256                             (index of all segments)
//
// Each segment is an independent .xz stream (CRC64 inside) with its own SHA-256 in the record,
// so damage is localised to a segment and therefore to a volume, interrupted jobs can resume at
// a segment boundary, and segments can be compressed in parallel.
// ---------------------------------------------------------------------------------------------

public static class FormatConst
{
    public const int HeaderSize = 64 * 1024;
    public const ushort FormatVersion = 3;
    public const int DefaultBlockSize = 4096;
    public const int SegmentRecordSize = 96;
    public const int VersionEndRecordSize = 56;
    public const int SfxTrailerSize = 16;
    public const int SfxAlignment = 4096;

    public static ReadOnlySpan<byte> HeaderMagic => "LZSN"u8;
    public static ReadOnlySpan<byte> SegmentMagic => "SEG1"u8;
    public static ReadOnlySpan<byte> VersionEndMagic => "VEND"u8;
    public static ReadOnlySpan<byte> FooterMagic => "FOOT"u8;
    public static ReadOnlySpan<byte> FooterEndMagic => "FEND"u8;
    public static ReadOnlySpan<byte> SfxMagic => "LZSNSFX1"u8;

    public const int KindXor = 0;
    public const int KindMotion = 1;

    public const byte TagSame = 0;
    public const byte TagMoved = 1;
    public const byte TagRaw = 2;
}

public sealed class VersionEntry
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public long LastWriteUtcTicks { get; set; }
}

public sealed class ArchiveHeader
{
    public bool Motion { get; set; }
    public bool Complete { get; set; }
    /// <summary>Files-and-folders archive: version 0 is the file data, version 1 the file table (see FolderArchiver).</summary>
    public bool Files { get; set; }
    public int BlockSize { get; set; } = FormatConst.DefaultBlockSize;
    public int SegmentSize { get; set; }
    public int Preset { get; set; }
    public long VolumeSize { get; set; }
    public int VolumeCount { get; set; }
    public long LogicalLength { get; set; }
    public long FooterOffset { get; set; }
    public Guid ArchiveId { get; set; }
    public string BaseName { get; set; } = "";
    public List<VersionEntry> Versions { get; } = new();

    public byte[] Serialize()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(FormatConst.HeaderMagic);
        w.Write(FormatConst.FormatVersion);
        w.Write((ushort)((Motion ? 1 : 0) | (Complete ? 2 : 0) | (Files ? 4 : 0)));
        w.Write(BlockSize);
        w.Write(SegmentSize);
        w.Write(Preset);
        w.Write(Versions.Count);
        w.Write(VolumeSize);
        w.Write(VolumeCount);
        w.Write(0);
        w.Write(LogicalLength);
        w.Write(FooterOffset);
        w.Write(ArchiveId.ToByteArray());
        WriteString(w, BaseName);
        foreach (var v in Versions)
        {
            w.Write(v.Size);
            w.Write(v.LastWriteUtcTicks);
            WriteString(w, v.Name);
        }
        w.Flush();
        if (ms.Length > FormatConst.HeaderSize - 32)
            throw new InvalidOperationException("Too many versions or names too long for the archive header.");
        var buf = new byte[FormatConst.HeaderSize];
        ms.GetBuffer().AsSpan(0, (int)ms.Length).CopyTo(buf);
        SHA256.HashData(buf.AsSpan(0, FormatConst.HeaderSize - 32), buf.AsSpan(FormatConst.HeaderSize - 32));
        return buf;
    }

    public static bool LooksLikeHeader(ReadOnlySpan<byte> buf) =>
        buf.Length >= FormatConst.HeaderSize &&
        buf[..4].SequenceEqual(FormatConst.HeaderMagic) &&
        BinaryPrimitives.ReadUInt16LittleEndian(buf[4..]) == FormatConst.FormatVersion &&
        SHA256.HashData(buf[..(FormatConst.HeaderSize - 32)]).AsSpan()
            .SequenceEqual(buf.Slice(FormatConst.HeaderSize - 32, 32));

    public static ArchiveHeader Parse(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FormatConst.HeaderSize || !buf[..4].SequenceEqual(FormatConst.HeaderMagic))
            throw new InvalidDataException("Not an LzFilm archive (.lzs).");
        ushort ver = BinaryPrimitives.ReadUInt16LittleEndian(buf[4..]);
        if (ver != FormatConst.FormatVersion)
            throw new InvalidDataException($"Unsupported archive format version {ver} (this program reads version {FormatConst.FormatVersion}).");
        if (!LooksLikeHeader(buf))
            throw new InvalidDataException("Archive header is damaged (checksum mismatch).");
        var r = new BinaryReader(new MemoryStream(buf[..(FormatConst.HeaderSize - 32)].ToArray()), Encoding.UTF8);
        r.ReadBytes(6);
        ushort flags = r.ReadUInt16();
        var h = new ArchiveHeader
        {
            Motion = (flags & 1) != 0,
            Complete = (flags & 2) != 0,
            Files = (flags & 4) != 0,
            BlockSize = r.ReadInt32(),
            SegmentSize = r.ReadInt32(),
            Preset = r.ReadInt32(),
        };
        int n = r.ReadInt32();
        h.VolumeSize = r.ReadInt64();
        h.VolumeCount = r.ReadInt32();
        r.ReadInt32();
        h.LogicalLength = r.ReadInt64();
        h.FooterOffset = r.ReadInt64();
        h.ArchiveId = new Guid(r.ReadBytes(16));
        h.BaseName = ReadString(r);
        if (n < 0 || n > 10000 || h.BlockSize <= 0 || h.SegmentSize <= 0 || h.SegmentSize % h.BlockSize != 0)
            throw new InvalidDataException("Archive header contains invalid values.");
        for (int i = 0; i < n; i++)
        {
            var v = new VersionEntry { Size = r.ReadInt64(), LastWriteUtcTicks = r.ReadInt64() };
            v.Name = ReadString(r);
            if (v.Size < 0)
                throw new InvalidDataException("Archive header contains an invalid version size.");
            h.Versions.Add(v);
        }
        return h;
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        if (b.Length > 4096)
            throw new InvalidOperationException("Name too long: " + s);
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    private static string ReadString(BinaryReader r)
    {
        int len = r.ReadUInt16();
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }
}

/// <summary>Block statistics of the motion coder (4 KB blocks).</summary>
public struct BlockStats
{
    public long Unchanged;
    public long ChangedInPlace;
    public long Moved;
    public long NoPrediction;

    public void Add(in BlockStats o)
    {
        Unchanged += o.Unchanged;
        ChangedInPlace += o.ChangedInPlace;
        Moved += o.Moved;
        NoPrediction += o.NoPrediction;
    }

    public readonly long Total => Unchanged + ChangedInPlace + Moved + NoPrediction;
}

public struct SegmentRecord
{
    public int Version;
    public long Index;
    public long RawOffset;
    public int RawLength;
    public int EncodedLength;
    public long CompressedLength;
    public int Kind;
    public BlockStats Stats;
    public byte[] PayloadSha256;

    public readonly void Write(Span<byte> d)
    {
        d[..FormatConst.SegmentRecordSize].Clear();
        FormatConst.SegmentMagic.CopyTo(d);
        BinaryPrimitives.WriteInt32LittleEndian(d[4..], Version);
        BinaryPrimitives.WriteInt64LittleEndian(d[8..], Index);
        BinaryPrimitives.WriteInt64LittleEndian(d[16..], RawOffset);
        BinaryPrimitives.WriteInt32LittleEndian(d[24..], RawLength);
        BinaryPrimitives.WriteInt32LittleEndian(d[28..], EncodedLength);
        BinaryPrimitives.WriteInt64LittleEndian(d[32..], CompressedLength);
        BinaryPrimitives.WriteInt32LittleEndian(d[40..], Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(d[44..], (uint)Stats.Unchanged);
        BinaryPrimitives.WriteUInt32LittleEndian(d[48..], (uint)Stats.ChangedInPlace);
        BinaryPrimitives.WriteUInt32LittleEndian(d[52..], (uint)Stats.Moved);
        BinaryPrimitives.WriteUInt32LittleEndian(d[56..], (uint)Stats.NoPrediction);
        PayloadSha256.CopyTo(d[64..]);
    }

    public static bool TryParse(ReadOnlySpan<byte> s, out SegmentRecord r)
    {
        r = default;
        if (s.Length < FormatConst.SegmentRecordSize || !s[..4].SequenceEqual(FormatConst.SegmentMagic))
            return false;
        r.Version = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
        r.Index = BinaryPrimitives.ReadInt64LittleEndian(s[8..]);
        r.RawOffset = BinaryPrimitives.ReadInt64LittleEndian(s[16..]);
        r.RawLength = BinaryPrimitives.ReadInt32LittleEndian(s[24..]);
        r.EncodedLength = BinaryPrimitives.ReadInt32LittleEndian(s[28..]);
        r.CompressedLength = BinaryPrimitives.ReadInt64LittleEndian(s[32..]);
        r.Kind = BinaryPrimitives.ReadInt32LittleEndian(s[40..]);
        r.Stats.Unchanged = BinaryPrimitives.ReadUInt32LittleEndian(s[44..]);
        r.Stats.ChangedInPlace = BinaryPrimitives.ReadUInt32LittleEndian(s[48..]);
        r.Stats.Moved = BinaryPrimitives.ReadUInt32LittleEndian(s[52..]);
        r.Stats.NoPrediction = BinaryPrimitives.ReadUInt32LittleEndian(s[56..]);
        r.PayloadSha256 = s.Slice(64, 32).ToArray();
        return r.Version >= 0 && r.Index >= 0 && r.RawOffset >= 0 && r.RawLength > 0 &&
               r.EncodedLength >= r.RawLength && r.CompressedLength > 0 &&
               (r.Kind == FormatConst.KindXor || r.Kind == FormatConst.KindMotion);
    }
}

public struct VersionEndRecord
{
    public int Version;
    public long Size;
    public long SegmentCount;
    public byte[] Sha256;

    public readonly void Write(Span<byte> d)
    {
        d[..FormatConst.VersionEndRecordSize].Clear();
        FormatConst.VersionEndMagic.CopyTo(d);
        BinaryPrimitives.WriteInt32LittleEndian(d[4..], Version);
        BinaryPrimitives.WriteInt64LittleEndian(d[8..], Size);
        BinaryPrimitives.WriteInt64LittleEndian(d[16..], SegmentCount);
        Sha256.CopyTo(d[24..]);
    }

    public static bool TryParse(ReadOnlySpan<byte> s, out VersionEndRecord r)
    {
        r = default;
        if (s.Length < FormatConst.VersionEndRecordSize || !s[..4].SequenceEqual(FormatConst.VersionEndMagic))
            return false;
        r.Version = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
        r.Size = BinaryPrimitives.ReadInt64LittleEndian(s[8..]);
        r.SegmentCount = BinaryPrimitives.ReadInt64LittleEndian(s[16..]);
        r.Sha256 = s.Slice(24, 32).ToArray();
        return true;
    }
}

public sealed class FooterVersion
{
    public long Size;
    public long SegmentCount;
    public byte[] Sha256 = Array.Empty<byte>();
}

public struct FooterSegment
{
    public int Version;
    public long Index;
    public long RecordOffset;
    public long CompressedLength;
}

public sealed class Footer
{
    public List<FooterVersion> Versions { get; } = new();
    public List<FooterSegment> Segments { get; } = new();
    public BlockStats Stats;

    public byte[] Serialize()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(FormatConst.FooterMagic);
        w.Write(Versions.Count);
        foreach (var v in Versions)
        {
            w.Write(v.Size);
            w.Write(v.SegmentCount);
            w.Write(v.Sha256);
        }
        w.Write((long)Segments.Count);
        foreach (var s in Segments)
        {
            w.Write(s.Version);
            w.Write(0);
            w.Write(s.Index);
            w.Write(s.RecordOffset);
            w.Write(s.CompressedLength);
        }
        w.Write(Stats.Unchanged);
        w.Write(Stats.ChangedInPlace);
        w.Write(Stats.Moved);
        w.Write(Stats.NoPrediction);
        w.Write(FormatConst.FooterEndMagic);
        w.Flush();
        var body = ms.ToArray();
        var res = new byte[body.Length + 32];
        body.CopyTo(res, 0);
        SHA256.HashData(body, res.AsSpan(body.Length));
        return res;
    }

    public static Footer Parse(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 4 + 4 + 8 + 32 + 4 || !buf[..4].SequenceEqual(FormatConst.FooterMagic))
            throw new InvalidDataException("Archive footer not found.");
        var body = buf[..^32];
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(buf[^32..]))
            throw new InvalidDataException("Archive footer is damaged (checksum mismatch).");
        var r = new BinaryReader(new MemoryStream(body.ToArray()));
        r.ReadBytes(4);
        var f = new Footer();
        int nv = r.ReadInt32();
        for (int i = 0; i < nv; i++)
            f.Versions.Add(new FooterVersion { Size = r.ReadInt64(), SegmentCount = r.ReadInt64(), Sha256 = r.ReadBytes(32) });
        long ns = r.ReadInt64();
        for (long i = 0; i < ns; i++)
        {
            var s = new FooterSegment { Version = r.ReadInt32() };
            r.ReadInt32();
            s.Index = r.ReadInt64();
            s.RecordOffset = r.ReadInt64();
            s.CompressedLength = r.ReadInt64();
            f.Segments.Add(s);
        }
        f.Stats.Unchanged = r.ReadInt64();
        f.Stats.ChangedInPlace = r.ReadInt64();
        f.Stats.Moved = r.ReadInt64();
        f.Stats.NoPrediction = r.ReadInt64();
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(FormatConst.FooterEndMagic))
            throw new InvalidDataException("Archive footer is malformed.");
        return f;
    }
}
