using System.Buffers.Binary;
using System.Numerics;
using Microsoft.Win32.SafeHandles;

namespace LzFilm.Core;

/// <summary>
/// Turns one segment of a version into a residual against the previous version, and back.
///
/// Kind XOR: residual = current XOR previous (same positions; the previous version is zero-padded).
/// Kind MOTION: per block a tag, then the block:
///   0 SAME  — XOR with the same position in the previous version
///   1 MOVED — u64 source offset, then XOR with that block of the previous version (all zeros)
///   2 RAW   — the block as is (when XOR would not produce at least 50% zeros)
/// </summary>
internal static class SegmentCodec
{
    public static int MaxEncodedLength(int rawLength, int blockSize) =>
        rawLength + ((rawLength + blockSize - 1) / blockSize) * 9;

    public static bool IsZero(ReadOnlySpan<byte> s)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int vs = Vector<byte>.Count;
            for (; i + vs <= s.Length; i += vs)
                if (!new Vector<byte>(s.Slice(i, vs)).Equals(Vector<byte>.Zero))
                    return false;
        }
        for (; i < s.Length; i++)
            if (s[i] != 0)
                return false;
        return true;
    }

    public static int CountNonZero(ReadOnlySpan<byte> s)
    {
        int n = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int vs = Vector<byte>.Count;
            for (; i + vs <= s.Length; i += vs)
            {
                var v = new Vector<byte>(s.Slice(i, vs));
                if (v.Equals(Vector<byte>.Zero))
                    continue;
                for (int j = 0; j < vs; j++)
                    if (v[j] != 0)
                        n++;
            }
        }
        for (; i < s.Length; i++)
            if (s[i] != 0)
                n++;
        return n;
    }

    /// <summary>dst = a XOR b; returns the number of non-zero bytes of dst.</summary>
    public static int XorCount(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst)
    {
        int i = 0, nz = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int vs = Vector<byte>.Count;
            for (; i + vs <= a.Length; i += vs)
            {
                var x = new Vector<byte>(a.Slice(i, vs)) ^ new Vector<byte>(b.Slice(i, vs));
                x.CopyTo(dst.Slice(i, vs));
                if (x.Equals(Vector<byte>.Zero))
                    continue;
                for (int j = 0; j < vs; j++)
                    if (x[j] != 0)
                        nz++;
            }
        }
        for (; i < a.Length; i++)
        {
            byte x = (byte)(a[i] ^ b[i]);
            dst[i] = x;
            if (x != 0)
                nz++;
        }
        return nz;
    }

    public static void XorInPlace(Span<byte> dst, ReadOnlySpan<byte> src)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int vs = Vector<byte>.Count;
            for (; i + vs <= dst.Length; i += vs)
                (new Vector<byte>(dst.Slice(i, vs)) ^ new Vector<byte>(src.Slice(i, vs))).CopyTo(dst.Slice(i, vs));
        }
        for (; i < dst.Length; i++)
            dst[i] ^= src[i];
    }

    public static int ReadAt(SafeFileHandle h, Span<byte> buf, long offset)
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

    /// <summary>Encodes a segment. Returns the residual length.</summary>
    public static int Encode(ReadOnlySpan<byte> cur, ReadOnlySpan<byte> prev, Span<byte> res, bool motion,
        long rawOffset, int blockSize, MotionIndex? prevIndex, SafeFileHandle? prevHandle, long prevLength,
        Span<byte> scratchBlock, ref BlockStats stats)
    {
        int n = cur.Length;
        if (!motion)
        {
            XorCount(cur, prev, res[..n]);
            return n;
        }
        int o = 0;
        for (int k = 0; k < n; k += blockSize)
        {
            int len = Math.Min(blockSize, n - k);
            var c = cur.Slice(k, len);
            var p = prev.Slice(k, len);
            int nzSame = XorCount(c, p, res.Slice(o + 1, len));
            if (nzSame == 0)
            {
                res[o] = FormatConst.TagSame;
                o += 1 + len;
                stats.Unchanged++;
                continue;
            }
            if (len == blockSize && prevIndex != null && prevHandle != null &&
                prevIndex.TryGet(MotionIndex.Hash(c), out uint blk))
            {
                long src = (long)blk * blockSize;
                if (src != rawOffset + k && src + blockSize <= prevLength &&
                    ReadAt(prevHandle, scratchBlock[..blockSize], src) == blockSize &&
                    scratchBlock[..blockSize].SequenceEqual(c))
                {
                    res[o] = FormatConst.TagMoved;
                    BinaryPrimitives.WriteInt64LittleEndian(res[(o + 1)..], src);
                    res.Slice(o + 9, len).Clear();
                    o += 9 + len;
                    stats.Moved++;
                    continue;
                }
            }
            // XOR only makes sense if it really produces zeros; new content written over unrelated
            // old bytes would turn into noise — then the block goes raw.
            if (nzSame * 2 > len || CountNonZero(c) < nzSame)
            {
                res[o] = FormatConst.TagRaw;
                c.CopyTo(res.Slice(o + 1, len));
                stats.NoPrediction++;
            }
            else
            {
                res[o] = FormatConst.TagSame;
                stats.ChangedInPlace++;
            }
            o += 1 + len;
        }
        return o;
    }

    /// <summary>Reconstructs a segment from its residual into <paramref name="output"/> (length = raw length).</summary>
    public static void Decode(ReadOnlySpan<byte> res, ReadOnlySpan<byte> prev, Span<byte> output, int kind,
        int blockSize, SafeFileHandle? prevHandle, long prevLength, Span<byte> scratchBlock)
    {
        int n = output.Length;
        if (kind == FormatConst.KindXor)
        {
            if (res.Length != n)
                throw new InvalidDataException("Residual length does not match the segment.");
            res.CopyTo(output);
            XorInPlace(output, prev);
            return;
        }
        int o = 0;
        for (int k = 0; k < n; k += blockSize)
        {
            int len = Math.Min(blockSize, n - k);
            if (o >= res.Length)
                throw new InvalidDataException("Residual is truncated.");
            byte tag = res[o++];
            var dst = output.Slice(k, len);
            switch (tag)
            {
                case FormatConst.TagSame:
                    res.Slice(o, len).CopyTo(dst);
                    XorInPlace(dst, prev.Slice(k, len));
                    o += len;
                    break;
                case FormatConst.TagMoved:
                    long src = BinaryPrimitives.ReadInt64LittleEndian(res[o..]);
                    o += 8;
                    if (prevHandle == null || src < 0 || src + len > prevLength ||
                        ReadAt(prevHandle, scratchBlock[..len], src) != len)
                        throw new InvalidDataException("A moved block points outside the previous version.");
                    res.Slice(o, len).CopyTo(dst);
                    XorInPlace(dst, scratchBlock[..len]);
                    o += len;
                    break;
                case FormatConst.TagRaw:
                    res.Slice(o, len).CopyTo(dst);
                    o += len;
                    break;
                default:
                    throw new InvalidDataException($"Unknown block tag {tag}.");
            }
        }
        if (o != res.Length)
            throw new InvalidDataException("Residual length does not match the segment.");
    }
}
