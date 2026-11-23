using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RimWise.Desktop.Services;

internal static class WindowFrameTheme
{
    // Retain native caption hit testing, Snap layouts, accessibility and DPI behavior.
    internal static void Apply(Window window, bool resetGlass = false)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        // Opacity is composited inside our opaque HWND, never with another app.
        // Reassert after custom-chrome/theme frame changes as well as at creation.
        if (!window.AllowsTransparency)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) Set(handle, 38, 1); // DWMSBT_NONE
            // Resetting glass invalidates the presented native surface. Only do it
            // before a new layout/render pass, never at the final reveal fence.
            if (resetGlass)
            {
                var margins = new Margins();
                _ = DwmExtendFrameIntoClientArea(handle, ref margins);
            }
        }
        // Until WPF submits its first frame (and during resize), the native surface
        // must clear to the same opaque color as the already-selected window theme.
        if (window.Background is SolidColorBrush { Color.A: 255 } background
            && HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
            if (target.BackgroundColor != background.Color) target.BackgroundColor = background.Color;
        var dark = Application.Current is App app && app.Themes is { IsDark: true } ? 1 : 0;
        Set(handle, 20, dark);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        foreach (var (attribute, key) in new[] { (35, "PanelBrush"), (36, "TextBrush"), (34, "BorderBrush") })
        {
            var color = SystemParameters.HighContrast ? -1 : attribute == 34 ? -2 /* DWMWA_COLOR_NONE: no decorative window outline */ : window.TryFindResource(key) is SolidColorBrush brush
                ? brush.Color.R | brush.Color.G << 8 | brush.Color.B << 16 : -1;
            Set(handle, attribute, color);
        }
    }

    private static void Set(IntPtr handle, int attribute, int value) =>
        _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
}
