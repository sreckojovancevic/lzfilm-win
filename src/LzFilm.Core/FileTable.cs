using System.Security.Cryptography;
using System.Text;

namespace LzFilm.Core;

/// <summary>One file or folder of a files-and-folders archive.</summary>
public sealed class FileTableEntry
{
    /// <summary>Path inside the archive, components separated by '/'.</summary>
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    /// <summary>Position of the file's data in the data stream (version 0).</summary>
    public long Offset { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public FileAttributes Attributes { get; set; }
    public byte[] Sha256 { get; set; } = new byte[32];

    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>
/// File table of a files-and-folders archive (stored as version 1). Layout, little-endian:
/// "LZFT" u32 ver=1 i32 count i64 dataLength, then per entry:
/// u8 flags(1=dir) u16 nameLen name(UTF-8) i64 size i64 offset i64 mtimeUtcTicks u32 attributes sha256[32].
/// </summary>
public sealed class FileTable
{
    private static ReadOnlySpan<byte> Magic => "LZFT"u8;

    public List<FileTableEntry> Entries { get; } = new();
    public long DataLength { get; set; }

    public IEnumerable<FileTableEntry> Files => Entries.Where(e => !e.IsDirectory);
    public IEnumerable<FileTableEntry> Directories => Entries.Where(e => e.IsDirectory);

    public byte[] Serialize(bool withHashes = true)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(1u);
        w.Write(Entries.Count);
        w.Write(DataLength);
        foreach (var e in Entries)
        {
            var name = Encoding.UTF8.GetBytes(e.Path);
            if (name.Length > ushort.MaxValue)
                throw new InvalidOperationException("Path too long: " + e.Path);
            w.Write((byte)(e.IsDirectory ? 1 : 0));
            w.Write((ushort)name.Length);
            w.Write(name);
            w.Write(e.Size);
            w.Write(e.Offset);
            w.Write(e.LastWriteUtcTicks);
            w.Write((uint)e.Attributes);
            w.Write(withHashes ? e.Sha256 : new byte[32]);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Identifies the list of files (names, sizes, times), independent of the hashes.</summary>
    public long Fingerprint() => BitConverter.ToInt64(SHA256.HashData(Serialize(withHashes: false)), 0) & long.MaxValue;

    public static FileTable Parse(byte[] data)
    {
        try
        {
            var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic) || r.ReadUInt32() != 1)
                throw new InvalidDataException("The file table of the archive is not valid.");
            int n = r.ReadInt32();
            var t = new FileTable { DataLength = r.ReadInt64() };
            if (n < 0 || t.DataLength < 0)
                throw new InvalidDataException("The file table of the archive is not valid.");
            for (int i = 0; i < n; i++)
            {
                var e = new FileTableEntry { IsDirectory = (r.ReadByte() & 1) != 0 };
                e.Path = Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
                e.Size = r.ReadInt64();
                e.Offset = r.ReadInt64();
                e.LastWriteUtcTicks = r.ReadInt64();
                e.Attributes = (FileAttributes)r.ReadUInt32();
                e.Sha256 = r.ReadBytes(32);
                if (e.Size < 0 || e.Offset < 0 || e.Offset + e.Size > t.DataLength || e.Sha256.Length != 32)
                    throw new InvalidDataException("The file table of the archive is not valid.");
                t.Entries.Add(e);
            }
            return t;
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("The file table of the archive is truncated.");
        }
    }
}

/// <summary>Result of scanning the files and folders to pack.</summary>
public sealed class FolderScan
{
    public FileTable Table { get; init; } = new();
    /// <summary>Full source path of every table entry (same index).</summary>
    public List<string> SourcePaths { get; init; } = new();
    /// <summary>Items that were not included, with the reason.</summary>
    public List<string> Skipped { get; init; } = new();

    public int FileCount => Table.Entries.Count(e => !e.IsDirectory);
    public int DirectoryCount => Table.Entries.Count(e => e.IsDirectory);
    public long TotalBytes => Table.DataLength;
}
