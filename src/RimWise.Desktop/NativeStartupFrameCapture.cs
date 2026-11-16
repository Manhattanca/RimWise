#if DEBUG
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RimWise.Desktop;

/// <summary>Reads only the synthetic test window's native client surface, never the desktop.</summary>
internal static class NativeStartupFrameCapture
{
    internal static bool IsAppCloaked(Window window)
    {
        var result = DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 14, out var flags, sizeof(int));
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        return (flags & 1) != 0;
    }

    internal static Color[] Capture(Window window, string path)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!GetClientRect(handle, out var bounds)) throw new InvalidOperationException("No native client bounds.");
        var width = bounds.Right;
        var height = bounds.Bottom;
        var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (dc == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            if (dc != IntPtr.Zero) DeleteDC(dc);
            throw new InvalidOperationException("Could not allocate native test bitmap.");
        }
        var previous = SelectObject(dc, bitmap);
        try
        {
            // PW_CLIENTONLY | PW_RENDERFULLCONTENT: only this HWND, even if occluded.
            if (!PrintWindow(handle, dc, 3)) throw new InvalidOperationException("Native test window capture failed.");
            var pixels = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(path)) encoder.Save(file);
            var dpi = VisualTreeHelper.GetDpi(window);
            Color Sample(int x)
            {
                var offset = ((height / 2) * width + (int)Math.Round(x * dpi.DpiScaleX)) * 4;
                return Color.FromRgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
            }
            // Page margin and empty interior of the left panel, away from text and borders.
            return [Sample(4), Sample(28)];
        }
        finally
        {
            SelectObject(dc, previous);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant, Colors;
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);
}
#endif
