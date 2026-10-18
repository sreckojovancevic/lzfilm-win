using System.Runtime.InteropServices;
using LzFilm.Core.Native;

namespace LzFilm.Core;

/// <summary>Single-call .xz compression and decompression (one complete .xz stream per buffer).</summary>
public static unsafe class Xz
{
    public static string Version
    {
        get
        {
            try { return Marshal.PtrToStringAnsi(LibLzma.lzma_version_string()) ?? "?"; }
            catch (DllNotFoundException) { return "not found"; }
        }
    }

    /// <summary>True if liblzma can be loaded.</summary>
    public static bool IsAvailable
    {
        get
        {
            try { _ = LibLzma.lzma_version_string(); return true; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    /// <summary>Approximate memory the encoder needs for a preset (bytes).</summary>
    public static long EncoderMemory(int preset) => (long)LibLzma.lzma_easy_encoder_memusage((uint)preset);

    /// <summary>Approximate memory the decoder needs for a preset (bytes).</summary>
    public static long DecoderMemory(int preset) => (long)LibLzma.lzma_easy_decoder_memusage((uint)preset);

    /// <summary>Worst-case size of the .xz stream for <paramref name="inputLength"/> bytes.</summary>
    public static int Bound(int inputLength) => checked((int)LibLzma.lzma_stream_buffer_bound((nuint)inputLength));

    /// <summary>Compresses <paramref name="input"/> into <paramref name="output"/>; returns the stream length.</summary>
    public static int Compress(ReadOnlySpan<byte> input, Span<byte> output, int preset)
    {
        if (preset < 0 || preset > 9)
            throw new ArgumentOutOfRangeException(nameof(preset));
        nuint pos = 0;
        fixed (byte* pi = input)
        fixed (byte* po = output)
        {
            int r = LibLzma.lzma_easy_buffer_encode((uint)preset, LibLzma.LZMA_CHECK_CRC64, IntPtr.Zero,
                pi, (nuint)input.Length, po, &pos, (nuint)output.Length);
            if (r != LibLzma.LZMA_OK)
                throw new InvalidOperationException($"liblzma encoder error {r}");
        }
        return (int)pos;
    }

    /// <summary>
    /// Decompresses one complete .xz stream. The stream must decode to exactly
    /// <paramref name="output"/>.Length bytes and use all of <paramref name="input"/>.
    /// Integrity is checked by liblzma (CRC64 inside the stream).
    /// </summary>
    public static void Decompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        ulong memlimit = ulong.MaxValue;
        nuint inPos = 0, outPos = 0;
        fixed (byte* pi = input)
        fixed (byte* po = output)
        {
            int r = LibLzma.lzma_stream_buffer_decode(&memlimit, 0, IntPtr.Zero,
                pi, &inPos, (nuint)input.Length, po, &outPos, (nuint)output.Length);
            if (r != LibLzma.LZMA_OK)
                throw new InvalidDataException(r switch
                {
                    9 => "compressed data is corrupt (LZMA_DATA_ERROR)",
                    7 => "not a valid .xz stream (LZMA_FORMAT_ERROR)",
                    10 => "decoded data does not fit the expected size (LZMA_BUF_ERROR)",
                    _ => $"liblzma decoder error {r}",
                });
        }
        if ((int)outPos != output.Length || (int)inPos != input.Length)
            throw new InvalidDataException("decoded length does not match the record");
    }
}
