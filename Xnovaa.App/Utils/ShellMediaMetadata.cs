using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Xnovaa.App.Utils;

/// <summary>
/// Reads media metadata (duration) via the Windows Shell property system —
/// the same COM surface the Shell thumbnail extractor uses. This avoids
/// libvlc entirely on the folder-import path: libvlc's mp4 demuxer can
/// access-violate or deadlock on malformed/truncated files, a native crash
/// that no managed handler can catch. The Shell property handlers run in-
/// process under Windows' own fault isolation and never take down the caller.
/// Duration is exposed in 100ns units (PROPERTYKEY System.Media.Duration).
/// </summary>
public static class ShellMediaMetadata
{
    // PROPERTYKEY System.Media.Duration
    // formatID {64440490-4C8B-11D1-8B70-080036B11A03}, propID 3, UInt64 in 100ns units.
    private static readonly Guid PKEY_Media_Duration_Guid =
        new(0x64440490, 0x4C8B, 0x11D1, 0x8B, 0x70, 0x08, 0x00, 0x36, 0xB1, 0x1A, 0x03);
    private const uint PKEY_Media_Duration_Pid = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [ComImport]
    [Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        // IShellItem
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
        // IShellItem2
        [PreserveSig] int GetPropertyStore(uint flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreWithCreateObject(uint flags, IntPtr punk, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, uint flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int Update(IntPtr pbc);
        [PreserveSig] int GetProperty(ref PROPERTYKEY key, out IntPtr ppropvar);
        [PreserveSig] int GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
        [PreserveSig] int GetFileTime(ref PROPERTYKEY key, out long pft);
        [PreserveSig] int GetInt32(ref PROPERTYKEY key, out int pi);
        [PreserveSig] int GetString(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
        [PreserveSig] int GetUInt32(ref PROPERTYKEY key, out uint pui);
        [PreserveSig] int GetUInt64(ref PROPERTYKEY key, out ulong pull);
        [PreserveSig] int GetBool(ref PROPERTYKEY key, out int pf);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem2 ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref IntPtr pvar);

    /// <summary>
    /// Returns the video duration, or null when unavailable (missing shell handler,
    /// unreadable file, unknown container). Never throws.
    /// </summary>
    public static TimeSpan? GetDuration(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

            var iid = typeof(IShellItem2).GUID;
            SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref iid, out var item);

            var key = new PROPERTYKEY { fmtid = PKEY_Media_Duration_Guid, pid = PKEY_Media_Duration_Pid };
            if (item.GetUInt64(ref key, out var duration100ns) != 0)
                return null;

            // 100ns units -> TimeSpan; guard against garbage values
            if (duration100ns == 0 || duration100ns > ulong.MaxValue / 2) return null;
            return TimeSpan.FromTicks((long)duration100ns);
        }
        catch
        {
            return null;
        }
    }
}
