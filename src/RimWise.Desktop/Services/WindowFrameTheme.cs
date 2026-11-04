using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RimWise.Desktop.Services;

internal static class WindowFrameTheme
{
    // Retain native caption hit testing, Snap layouts, accessibility and DPI behavior.
    internal static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = Application.Current is App app && app.Themes is { IsDark: true } ? 1 : 0;
        Set(handle, 20, dark);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        foreach (var (attribute, key) in new[] { (35, "PanelBrush"), (36, "TextBrush"), (34, "BorderBrush") })
        {
            var color = SystemParameters.HighContrast ? -1 : window.TryFindResource(key) is SolidColorBrush brush
                ? brush.Color.R | brush.Color.G << 8 | brush.Color.B << 16 : -1;
            Set(handle, attribute, color);
        }
    }

    private static void Set(IntPtr handle, int attribute, int value) =>
        _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
