using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LzFilm.Core.Native;

/// <summary>
/// Minimal binding to liblzma (xz-utils). Only the single-call buffer API is used, so no
/// lzma_stream structure has to be marshalled.
/// </summary>
internal static unsafe class LibLzma
{
    internal const string Lib = "liblzma";

    internal const int LZMA_OK = 0;
    internal const int LZMA_CHECK_CRC64 = 4;

#pragma warning disable CA2255 // the resolver must be registered before the first P/Invoke from any host
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibLzma).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Lib)
            return IntPtr.Zero;
        // Default probing first (app directory, bundled single-file content), then common names:
        // liblzma.dll (CMake/MSVC), liblzma-5.dll (MinGW), liblzma.so.5 (Linux), liblzma.5.dylib (macOS).
        string[] candidates = { Lib, "liblzma-5", "lzma", "liblzma.so.5", "liblzma.5.dylib" };
        foreach (var c in candidates)
        {
            if (NativeLibrary.TryLoad(c, assembly, searchPath, out var h))
                return h;
            var local = Path.Combine(AppContext.BaseDirectory, c);
            if (NativeLibrary.TryLoad(local, out h))
                return h;
            if (NativeLibrary.TryLoad(local + ".dll", out h))
                return h;
        }
        return IntPtr.Zero;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lzma_easy_buffer_encode(uint preset, int check, IntPtr allocator,
        byte* input, nuint inSize, byte* output, nuint* outPos, nuint outSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lzma_stream_buffer_decode(ulong* memlimit, uint flags, IntPtr allocator,
        byte* input, nuint* inPos, nuint inSize, byte* output, nuint* outPos, nuint outSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nuint lzma_stream_buffer_bound(nuint uncompressedSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong lzma_easy_encoder_memusage(uint preset);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong lzma_easy_decoder_memusage(uint preset);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr lzma_version_string();
}
