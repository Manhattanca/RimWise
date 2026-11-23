#if DEBUG
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using RimWise.Desktop.Services;

namespace RimWise.Desktop;

internal static class StartupNativeSurfaceSmokeChecks
{
    internal static Task Run(MainWindow main, string output, Action<bool, string> check)
    {
        var hwnd = new WindowInteropHelper(main).Handle;
        var target = HwndSource.FromHwnd(hwnd)!.CompositionTarget;
        var points = new Point[] { new(main.ActualWidth - 180, 12), new(main.ActualWidth - 180, 44), new(main.ActualWidth - 180, 90) };
        var actual = NativeStartupFrameCapture.Capture(main, Path.Combine(output, "native-top-first-frame.png"), points);
        var expected = NativeStartupFrameCapture.ExpectedCompositedSamples(main, Path.Combine(output, "expected-top-first-frame.png"), points);
        for (var i = 0; i < points.Length; i++)
            check(Difference(actual[i], expected[i]) <= 3, "first-frame title/menu/navigation pixels contain only the composed app scene: " + i);
        // Simulate a framework/native frame reset; only this synthetic HWND is changed.
        target.BackgroundColor = Colors.Transparent;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        WindowFrameTheme.Apply(main, resetGlass: true);
        check(target.BackgroundColor.A == 255, "native frame reset is repaired to an opaque theme clear color");
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
            && DwmGetWindowAttribute(hwnd, 38, out var backdrop, sizeof(int)) >= 0)
            check(backdrop == 1, "native reset cannot leave a system backdrop behind the custom title");
        return Task.CompletedTask;
    }
    private static int Difference(Color a, Color b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr handle, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
#endif
