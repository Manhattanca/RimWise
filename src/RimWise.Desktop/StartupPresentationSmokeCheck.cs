#if DEBUG
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

/// <summary>Checks the native surface before Loaded, not just an already-painted window.</summary>
internal static class StartupPresentationSmokeCheck
{
    internal static void Attach(MainWindow window, string output)
    {
        var checks = new List<string>();
        var failures = new List<string>();
        var app = (App)Application.Current;
        var initialTheme = app.Themes.Preference;
        var width = window.Width;
        var height = window.Height;
        var initialized = false;
        var loaded = false;
        var firstRender = true;
        var unavailableNativeColorReads = 0;
        var revealFenceObserved = false;
        Directory.CreateDirectory(output);

        void Check(bool condition, string name)
        {
            checks.Add(name);
            if (!condition) failures.Add(name);
        }

        void CheckSurface(string stage)
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            Check(window.Background is SolidColorBrush brush
                && source?.CompositionTarget?.BackgroundColor == brush.Color, stage + ": native clear color matches theme");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                var result = DwmGetWindowAttribute(source!.Handle, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, out var backdrop, sizeof(int));
                Check(result == 0 && backdrop is 0 or 1, stage + ": opaque application does not extend a system backdrop into its client area");
            }
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemParameters.HighContrast) return;
            foreach (var (attribute, key) in new[] { (35, "PanelBrush"), (36, "TextBrush"), (34, "BorderBrush") })
            {
                var color = ((SolidColorBrush)window.FindResource(key)).Color;
                var result = DwmGetWindowAttribute(source!.Handle, attribute, out var actual, sizeof(int));
                // Some Windows/DWM versions expose these attributes for Set only.
                // An unavailable getter cannot verify caption color; report that limit.
                if (result != 0) { unavailableNativeColorReads++; continue; }
                Check(actual == (attribute == 34 ? -2 : color.R | color.G << 8 | color.B << 16), stage + ": native " + key);
            }
        }

        window.StartupPresentation.BeforeReveal += () =>
        {
            Check(NativeStartupFrameCapture.IsAppCloaked(window), "native window still concealed at render fence");
            Check(HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.CompositionTarget.BackgroundColor.A == 255,
                "custom chrome leaves an opaque native clear surface immediately before reveal");
            revealFenceObserved = true;
        };

        window.SourceInitialized += (_, _) =>
        {
            initialized = true;
            Check(!window.IsLoaded, "native theme applied before Loaded");
            CheckSurface("SourceInitialized");
            Check(NativeStartupFrameCapture.IsAppCloaked(window), "native window concealed before first Show");
        };
        window.Loaded += (_, _) =>
        {
            loaded = true;
            Check(initialized, "native source ready before Loaded");
            CheckSurface("Loaded");
            Check(NativeStartupFrameCapture.IsAppCloaked(window), "native window concealed while first layout runs");
        };
        window.ContentRendered += async (_, _) =>
        {
            if (!firstRender) return;
            firstRender = false;
            try
            {
                Check(NativeStartupFrameCapture.IsAppCloaked(window), "ContentRendered does not expose an unsubmitted native surface");
                Check(await window.StartupPresentation.Ready, "native presentation completed");
                Check(AppContext.TryGetSwitch("Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop", out var disabled) && disabled,
                    "Fluent controls retain their theme without enabling transparent window backdrops");
                // Capture after release, without adding a bitmap render, image write or
                // fixed delay before the production reveal path can show the window.
                var nativeSamples = NativeStartupFrameCapture.Capture(window, Path.Combine(output, "native-first-frame.png"));
                Check(!NativeStartupFrameCapture.IsAppCloaked(window), "native window revealed after render fence");
                Check(initialized && loaded, "native source and layout ready before first frame");
                Check(app.MainWindow == window && app.Windows.Count == 1, "first visible window is the only main window");
                Check(window.Width == width && window.Height == height && window.ActualWidth >= window.MinWidth
                    && window.ActualHeight >= window.MinHeight, "first frame uses main window size");
                Check(window.Opacity == 1 && !window.HasAnimatedProperties
                    && window.Content is UIElement { Opacity: 1, HasAnimatedProperties: false }, "first frame is fully visible without a reveal animation");
                Check(window.OrderGrid.ActualWidth > 0 && window.AvailableGrid.ActualWidth > 0
                    && window.MainMenu.ActualHeight > 0, "main controls laid out on first frame");
                CheckSurface("ContentRendered");
                Check(revealFenceObserved, "native concealment verified immediately before reveal");
                await StartupNativeSurfaceSmokeChecks.Run(window, output, Check);
                var expectedSamples = new[] { ((SolidColorBrush)window.FindResource("PageBrush")).Color,
                    ((SolidColorBrush)window.FindResource("PanelBrush")).Color };
                // Translucent cards also composite over a solid page without a picture.
                // Compare their displayed pixels against an independent WPF render;
                // genuinely opaque solid themes retain the exact palette assertion.
                var composited = !SystemParameters.HighContrast
                    && (app.Themes.Image is not null || app.Themes.Current.PanelOpacity < 1);
                if (composited)
                    expectedSamples = NativeStartupFrameCapture.ExpectedCompositedSamples(window, Path.Combine(output, "expected-native-frame.png"));
                // Hardware and offscreen alpha blending may differ by one 8-bit
                // channel step; solid themes still require the original exact match.
                var matches = composited ? nativeSamples.Zip(expectedSamples).All(pair =>
                    Math.Abs(pair.First.R - pair.Second.R) <= 1 && Math.Abs(pair.First.G - pair.Second.G) <= 1
                    && Math.Abs(pair.First.B - pair.Second.B) <= 1) : nativeSamples.SequenceEqual(expectedSamples);
                Check(matches, "native first frame contains themed page and panel pixels: "
                    + string.Join(", ", nativeSamples.Select(color => color.ToString()))
                    + "; expected " + string.Join(", ", expectedSamples.Select(color => color.ToString())));
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, "first-frame.png"));

                window.LoadSample();
                var order = window.ViewModel.Rows.Select(row => row.PackageId).ToArray();
                foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark, ThemePreference.System })
                {
                    app.Themes.Select(theme, save: false);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    CheckSurface("Live " + theme);
                    Check(order.SequenceEqual(window.ViewModel.Rows.Select(row => row.PackageId)), "theme preserves order " + theme);
                }
                foreach (var state in new[] { WindowState.Maximized, WindowState.Normal, WindowState.Minimized, WindowState.Normal })
                {
                    window.WindowState = state;
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    CheckSurface("Window state " + state);
                    Check(order.SequenceEqual(window.ViewModel.Rows.Select(row => row.PackageId)), "window state preserves order " + state);
                }
                app.Themes.Select(initialTheme, save: false);
                Check(new AppearanceSettingsStore(UserStorage.DirectoryPath).Load() == initialTheme, "saved theme unchanged");
                await CheckLifecycleAsync(window, Check);
                window.ViewModel.MarkExported();
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    Passed = failures.Count == 0, InitialTheme = initialTheme.ToString(), Checks = checks, Failures = failures,
                    UnavailableNativeColorReads = unavailableNativeColorReads,
                    RealAiRequest = false, RealGameRun = false, RealUserDataUsed = false
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failures.Count == 0 ? 0 : 1);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                window.ViewModel.MarkExported();
                app.Shutdown(1);
            }
        };
    }

    private static async Task CheckLifecycleAsync(MainWindow owner, Action<bool, string> check)
    {
        Window Offscreen() => new()
        {
            Owner = owner, Width = 300, Height = 160, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false,
            Content = new System.Windows.Controls.TextBlock { Text = "Synthetic startup lifecycle" }
        };

        // A close after Show but before the first ContentRendered must release every
        // waiter and the static Rendering subscription without exposing a dead HWND.
        var closing = Offscreen();
        var closeGate = new StartupWindowPresentation(closing);
        var revealedAfterClose = false;
        closeGate.BeforeReveal += () => revealedAfterClose = true;
        closing.Show();
        closing.Close();
        check(!await closeGate.Ready.WaitAsync(TimeSpan.FromSeconds(3)), "close before first frame completes pending presentation");
        await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(!revealedAfterClose, "closed window is never revealed later");

        var pendingRender = Offscreen();
        var pendingGate = new StartupWindowPresentation(pendingRender);
        var pendingRevealed = false;
        pendingGate.BeforeReveal += () => pendingRevealed = true;
        pendingRender.ContentRendered += (_, _) => pendingRender.Close();
        pendingRender.Show();
        check(!await pendingGate.Ready.WaitAsync(TimeSpan.FromSeconds(3)), "close while waiting for native render completes presentation");
        await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(!pendingRevealed, "pending render callback cannot reveal a closed window");

        // If cloaking is unsupported, retain normal startup rather than hiding the
        // window indefinitely or attempting an unmatched uncloak operation.
        var fallback = Offscreen();
        var fallbackCalls = new List<bool>();
        var fallbackGate = new StartupWindowPresentation(fallback, (_, enabled) =>
        {
            fallbackCalls.Add(enabled);
            return unchecked((int)0x80004005);
        }, () => throw new InvalidOperationException("Flush called without a cloak."));
        fallback.Show();
        try
        {
            check(await fallbackGate.Ready.WaitAsync(TimeSpan.FromSeconds(3)), "unavailable cloak falls back to ordinary startup");
            check(fallbackCalls.SequenceEqual(new[] { true }), "unavailable cloak does not invoke unmatched release");
        }
        finally { fallback.Close(); }

        var flushFailure = Offscreen();
        var releaseCalls = new List<bool>();
        var flushCalls = 0;
        var flushGate = new StartupWindowPresentation(flushFailure, (_, enabled) =>
        {
            releaseCalls.Add(enabled);
            return 0;
        }, () => { flushCalls++; return unchecked((int)0x80004005); });
        flushFailure.Show();
        try
        {
            check(await flushGate.Ready.WaitAsync(TimeSpan.FromSeconds(3)), "failed flush still completes presentation");
            check(flushCalls == 1 && releaseCalls.SequenceEqual(new[] { true, false }), "failed flush still releases the cloak exactly once");
        }
        finally { flushFailure.Close(); }

        var releaseFailure = Offscreen();
        var releaseGate = new StartupWindowPresentation(releaseFailure,
            (_, enabled) => enabled ? 0 : unchecked((int)0x80004005), () => 0);
        releaseFailure.Show();
        try
        {
            check(!await releaseGate.Ready.WaitAsync(TimeSpan.FromSeconds(3)), "failed native release is reported to startup rather than falsely marked visible");
        }
        finally { releaseFailure.Close(); }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
#endif
