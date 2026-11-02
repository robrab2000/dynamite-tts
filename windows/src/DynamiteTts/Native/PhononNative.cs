using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DynamiteTts.Native;

/// <summary>
/// P/Invoke surface of <c>dynamite_phonon.dll</c>, the Rust shim over Gradium's Phonon runtime
/// (<c>windows/native/phonon</c>; header <c>include/dynamite_phonon.h</c>). Booleans are 32-bit
/// integers on both sides so there is no C <c>bool</c> width to get wrong.
/// </summary>
internal static unsafe class PhononNative
{
    public const string LibraryName = "dynamite_phonon";
    public const string LibraryFileName = LibraryName + ".dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct DpResult
    {
        public nuint Samples;
        public double TtfaMs;
        public double TotalMs;
    }

    /// <summary>Where the DLL is expected: next to the executable.</summary>
    public static string LibraryPath => Path.Combine(AppContext.BaseDirectory, LibraryFileName);

    public static bool IsLibraryPresent() => File.Exists(LibraryPath);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr dp_new(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string lang,
        uint threads);

    /// <param name="cb">A <c>delegate* unmanaged[Cdecl]&lt;float*, nuint, void*, int&gt;</c> cast to IntPtr.</param>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dp_speak(
        IntPtr h,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? voice,
        float temperature,
        IntPtr cb,
        IntPtr user,
        out DpResult result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dp_add_voice(
        IntPtr h,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr dp_voices(IntPtr h);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dp_set_language(IntPtr h, [MarshalAs(UnmanagedType.LPUTF8Str)] string lang);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dp_set_duration_delta(IntPtr h, float durationDelta);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern uint dp_sample_rate(IntPtr h);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr dp_last_error(IntPtr h);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void dp_free(IntPtr h);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr dp_runtime_version();

    /// <summary>The last error for <paramref name="h"/> (or the last failed load on this thread when zero).</summary>
    public static string LastError(IntPtr h)
    {
        var p = dp_last_error(h);
        return p == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringUTF8(p) ?? "unknown error";
    }

    public static string RuntimeVersion()
    {
        try { return Marshal.PtrToStringUTF8(dp_runtime_version()) ?? "?"; }
        catch { return "?"; }
    }

    /// <summary>Decodes the NUL-separated, double-NUL-terminated voice list.</summary>
    public static string[] Voices(IntPtr h)
    {
        var p = (byte*)dp_voices(h);
        if (p == null) return Array.Empty<string>();
        var names = new System.Collections.Generic.List<string>();
        while (*p != 0)
        {
            var len = 0;
            while (p[len] != 0) len++;
            names.Add(System.Text.Encoding.UTF8.GetString(p, len));
            p += len + 1;
        }
        return names.ToArray();
    }
}
