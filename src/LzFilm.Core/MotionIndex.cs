using System.Numerics;
using System.Runtime.InteropServices;

namespace LzFilm.Core;

/// <summary>
/// Content index of a version's blocks: block hash → first block number with that content.
/// Open addressing, 12 bytes per slot. The table never grows beyond its memory limit; when it is
/// full, further blocks are not indexed (<see cref="Truncated"/>), which only costs ratio.
/// </summary>
internal sealed class MotionIndex
{
    private readonly ulong[] _keys;
    private readonly uint[] _vals;
    private readonly int _mask;
    private readonly int _shift;
    private readonly int _maxCount;
    private int _count;

    public bool Truncated { get; private set; }
    public int Count => _count;
    public long MemoryBytes => _keys.LongLength * 12;

    public MotionIndex(long blocks, long memoryLimit)
    {
        long want = 16;
        while (want < blocks * 2 && want < (1L << 30))
            want <<= 1;
        long maxSlots = Math.Max(16, memoryLimit / 12);
        while (want > maxSlots && want > 16)
            want >>= 1;
        _keys = new ulong[want];
        _vals = new uint[want];
        _mask = (int)(want - 1);
        _shift = 64 - BitOperations.Log2((ulong)want);
        _maxCount = (int)(want * 7 / 10);
    }

    public static long EstimateBytes(long versionBytes, int blockSize, long memoryLimit)
    {
        long blocks = versionBytes / blockSize;
        long want = 16;
        while (want < blocks * 2 && want < (1L << 30))
            want <<= 1;
        long maxSlots = Math.Max(16, memoryLimit / 12);
        while (want > maxSlots && want > 16)
            want >>= 1;
        return want * 12;
    }

    private int Slot(ulong h) => (int)((h * 0x9E3779B97F4A7C15UL) >> _shift) & _mask;

    /// <summary>Adds a block if its content is not indexed yet (first occurrence wins).</summary>
    public void AddFirst(ulong hash, uint block)
    {
        if (hash == 0)
            hash = 1;
        int i = Slot(hash);
        while (_keys[i] != 0)
        {
            if (_keys[i] == hash)
                return;
            i = (i + 1) & _mask;
        }
        if (_count >= _maxCount)
        {
            Truncated = true;
            return;
        }
        _keys[i] = hash;
        _vals[i] = block;
        _count++;
    }

    public bool TryGet(ulong hash, out uint block)
    {
        if (hash == 0)
            hash = 1;
        int i = Slot(hash);
        while (_keys[i] != 0)
        {
            if (_keys[i] == hash)
            {
                block = _vals[i];
                return true;
            }
            i = (i + 1) & _mask;
        }
        block = 0;
        return false;
    }

    /// <summary>64-bit hash of a block (block length must be a multiple of 8).</summary>
    public static ulong Hash(ReadOnlySpan<byte> block)
    {
        var w = MemoryMarshal.Cast<byte, ulong>(block);
        ulong h = 0xcbf29ce484222325UL;
        foreach (var x in w)
        {
            h ^= x;
            h = BitOperations.RotateLeft(h * 0x100000001b3UL, 23);
        }
        return h;
    }

    /// <summary>Indexes all full, non-zero blocks of a buffer that starts at block <paramref name="firstBlock"/>.</summary>
    public void AddBuffer(ReadOnlySpan<byte> data, long firstBlock, int blockSize)
    {
        int full = data.Length / blockSize;
        for (int b = 0; b < full; b++)
        {
            var blk = data.Slice(b * blockSize, blockSize);
            if (SegmentCodec.IsZero(blk))
                continue;
            long n = firstBlock + b;
            if (n > uint.MaxValue)
            {
                Truncated = true;
                return;
            }
            AddFirst(Hash(blk), (uint)n);
        }
    }
}
