using System.IO;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xnovaa.App.Utils;

/// <summary>
/// Lightweight native thumbnail extraction using the Windows Shell
/// (IShellItemImageFactory). No extra window, no heavy decoder — the Shell
/// uses the OS media handlers (Media Foundation backed on Win10).
/// </summary>
public static class NativeThumbnail
{
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8fdc592d9923")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            SIZE size,
            SIIGBF flags,
            out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [Flags]
    private enum SIIGBF : uint
    {
        ResizeToFit = 0x00000000,
        BiggerSizeOk = 0x00000001,
        MemoryOnly = 0x00000002,
        IconOnly = 0x00000004,
        ThumbnailOnly = 0x00000008,
        InCacheOnly = 0x00000010
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>Returns a decoded, frozen thumbnail or null on failure.</summary>
    public static BitmapSource? Extract(string filePath, int maxPixelWidth = 160)
    {
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            var guid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref guid, out var factoryObj);
            var factory = (IShellItemImageFactory)factoryObj;

            // Ask slightly larger than target then scale down for quality.
            var size = new SIZE(maxPixelWidth * 2, maxPixelWidth * 2);
            var hr = factory.GetImage(size, SIIGBF.ThumbnailOnly | SIIGBF.BiggerSizeOk, out hBitmap);
            if (hr != 0)
            {
                // Fall back to any representation.
                hr = factory.GetImage(size, SIIGBF.ResizeToFit, out hBitmap);
                if (hr != 0) return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            DeleteObject(hBitmap);
            hBitmap = IntPtr.Zero;

            if (source.PixelWidth > maxPixelWidth)
            {
                var scale = maxPixelWidth / (double)source.PixelWidth;
                var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                scaled.Freeze();
                return scaled;
            }
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
        }
    }
}
