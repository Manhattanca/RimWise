using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace RimWise.Desktop.Services;

/// <summary>Extends the client's single background under the caption without making the HWND translucent.</summary>
internal static class MainWindowChrome
{
    internal static Task Attach(MainWindow window)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => ready.TrySetResult();
        var chrome = new WindowChrome { CaptionHeight = 30, GlassFrameThickness = new(0),
            CornerRadius = new(8), ResizeBorderThickness = new(6), UseAeroCaptionButtons = false };
        window.CaptionMinimize.Click += (_, _) => SystemCommands.MinimizeWindow(window);
        window.CaptionMaximize.Click += (_, _) => Toggle(window);
        window.CaptionClose.Click += (_, _) => SystemCommands.CloseWindow(window);
        void StateChanged(object? sender, EventArgs args)
        {
            var max = window.WindowState == WindowState.Maximized;
            window.MainClient.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
            window.CaptionMaximize.Content = max ? "\uE923" : "\uE922";
            window.CaptionMaximize.ToolTip = max ? "还原" : "最大化";
        }
        window.StateChanged += StateChanged;
        window.SourceInitialized += (_, _) => window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, new Action(() =>
        {
            try
            {
            // Native frame changes can reenter Loaded. Finish source observers first;
            // presentation explicitly waits for this entire transaction before its render fence.
            if (window.Dispatcher.HasShutdownStarted) return;
            WindowChrome.SetWindowChrome(window, chrome);
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            if (source is null) return;
            source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message is 0x031E or 0x031A) // DWM composition or Windows theme changed.
                    window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                        new Action(() => { if (window.IsVisible) { WindowFrameTheme.Apply(window, resetGlass: true); window.InvalidateVisual(); } }));
                // HTMAXBUTTON preserves Windows 11's native Snap-layout affordance.
                if (message == 0x0084 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                    && window.ResizeMode == ResizeMode.CanResize)
                {
                    var packed = lParam.ToInt64();
                    var point = window.CaptionMaximize.PointFromScreen(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
                    if (point.X >= 0 && point.Y >= 0 && point.X < window.CaptionMaximize.ActualWidth && point.Y < window.CaptionMaximize.ActualHeight)
                    { handled = true; return new IntPtr(9); }
                }
                if (message is 0x00A1 or 0x00A2 && wParam.ToInt32() == 9)
                {
                    handled = true;
                    if (message == 0x00A2) Toggle(window);
                }
                return IntPtr.Zero;
            });
            StateChanged(null, EventArgs.Empty);
            WindowFrameTheme.Apply(window, resetGlass: true);
            }
            finally { ready.TrySetResult(); }
        }));
        return ready.Task;
    }
    private static void Toggle(Window window)
    {
        if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
        else SystemCommands.MaximizeWindow(window);
    }
}
