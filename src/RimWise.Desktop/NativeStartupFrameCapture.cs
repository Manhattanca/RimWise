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
    internal static void FlushComposition()
    {
        var result = DwmFlush();
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }
    internal static bool IsAppCloaked(Window window)
    {
        var result = DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 14, out var flags, sizeof(int));
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        return (flags & 1) != 0;
    }

    internal static Color[] CaptureEdges(Window window, string path)
    {
        if (!GetClientRect(new WindowInteropHelper(window).Handle, out var bounds))
            throw new InvalidOperationException("No native client bounds.");
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = bounds.Right / dpi.DpiScaleX; var height = bounds.Bottom / dpi.DpiScaleY;
        return Capture(window, path, [new(4, 4), new(width / 2, 4), new(width - 5, 4),
            new(4, height / 2), new(width - 5, height / 2), new(4, height - 5), new(width / 2, height - 5), new(width - 5, height - 5)]);
    }

    internal static Color[] Capture(Window window, string path, IReadOnlyList<System.Windows.Point>? samplePoints = null)
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
            Color Sample(System.Windows.Point point)
            {
                var x = (int)Math.Round(point.X * dpi.DpiScaleX);
                var y = (int)Math.Round(point.Y * dpi.DpiScaleY);
                if (x < 0 || x >= width || y < 0 || y >= height) throw new ArgumentOutOfRangeException(nameof(samplePoints));
                var offset = (y * width + x) * 4;
                return Color.FromRgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
            }
            // Page margin and empty interior of the left panel, away from text and borders.
            return (samplePoints ?? [new(4, (height / 2) / dpi.DpiScaleY), new(28, (height / 2) / dpi.DpiScaleY)])
                .Select(Sample).ToArray();
        }
        finally
        {
            SelectObject(dc, previous);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    internal static Color[] ExpectedCompositedSamples(Window window, string path, IReadOnlyList<System.Windows.Point>? samplePoints = null)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!GetClientRect(handle, out var bounds)) throw new InvalidOperationException("No native client bounds.");
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap(bounds.Right, bounds.Bottom, 96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
            context.DrawRectangle(window.Background, null, new System.Windows.Rect(0, 0, bounds.Right / dpi.DpiScaleX, bounds.Bottom / dpi.DpiScaleY));
        bitmap.Render(background); bitmap.Render((Visual)window.Content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        Color Sample(System.Windows.Point point)
        {
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)Math.Round(point.X * dpi.DpiScaleX),
                (int)Math.Round(point.Y * dpi.DpiScaleY), 1, 1), pixel, 4, 0);
            return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        }
        return (samplePoints ?? [new(4, bounds.Bottom / dpi.DpiScaleY / 2), new(28, bounds.Bottom / dpi.DpiScaleY / 2)])
            .Select(Sample).ToArray();
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
    private static extern int DwmFlush();
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
