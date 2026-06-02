using System;
using System.Runtime.InteropServices;

namespace Tiger;

/// <summary>P/Invoke wrapper for Oodle (Kraken) decompression via oo2core_9_win64.dll.</summary>
public static class Oodle
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long OodleLZ_DecompressDelegate(
        byte[] compBuf, long compBufSize,
        byte[] rawBuf, long rawLen,
        int fuzzSafe, int checkCrc, int verbosity,
        IntPtr decBufBase, IntPtr decBufSize,
        IntPtr fpCallback, IntPtr callbackUserData,
        IntPtr decoderMemory, IntPtr decoderMemorySize,
        int threadPhase);

    private static OodleLZ_DecompressDelegate? _decompress;
    private static readonly object _lock = new();

    public static void Initialize(string dllPath)
    {
        lock (_lock)
        {
            if (_decompress != null) return;
            IntPtr h = LoadLibrary(dllPath);
            if (h == IntPtr.Zero)
                throw new DllNotFoundException($"Failed to load Oodle: {dllPath} (err {Marshal.GetLastWin32Error()})");
            IntPtr proc = GetProcAddress(h, "OodleLZ_Decompress");
            if (proc == IntPtr.Zero)
                throw new EntryPointNotFoundException("OodleLZ_Decompress not found in DLL");
            _decompress = Marshal.GetDelegateForFunctionPointer<OodleLZ_DecompressDelegate>(proc);
        }
    }

    public static byte[] Decompress(byte[] comp, int outSize)
    {
        if (_decompress == null) throw new InvalidOperationException("Oodle not initialized");
        byte[] outBuf = new byte[outSize];
        long n = _decompress(comp, comp.Length, outBuf, outSize, 1, 0, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 3);
        if (n <= 0) throw new InvalidOperationException($"Oodle decompress failed (ret={n})");
        if (n == outSize) return outBuf;
        byte[] trimmed = new byte[n];
        Array.Copy(outBuf, trimmed, n);
        return trimmed;
    }
}
