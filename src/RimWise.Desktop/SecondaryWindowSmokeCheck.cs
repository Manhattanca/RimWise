#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;
using RimWise.Integration.Marketplace;

namespace RimWise.Desktop;

internal static class SecondaryWindowSmokeCheck
{
    internal static async Task RunAsync(MainWindow owner, string output, List<string> checks)
    {
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        var app = (App)Application.Current;
        var preference = app.Themes.Preference;
        var manifest = new MarketplaceManifest(1, "frame-fixture", "1.0.0", "窗口首帧验收", "unpublished", "", "1.7", [], null, [new("ludeon.rimworld", null)]);
        try
        {
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                app.Themes.Select(theme, save: false);
                foreach (var modal in new[] { false, true })
                {
                    Window dialog = modal ? new MarketplaceMyOrdersWindow([], []) : new MarketplacePublishWindow(manifest,
                        new MarketplacePublishActions { LoginAsync = (_, _, _) => throw new InvalidOperationException(),
                            SubmitAsync = (_, _, _) => throw new InvalidOperationException(), Logout = () => { }, OpenTrustedLink = _ => { } });
                    dialog.Owner = owner;
                    var gate = SecondaryWindowPresentation.GetPresentation(dialog);
                    Check(gate is not null, $"Secondary window installs frame preparation before Show: {theme}, modal={modal}");
                    var earlyTheme = false;
                    var earlyCloak = false;
                    var fullFrame = false;
                    dialog.SourceInitialized += (_, _) =>
                    {
                        var target = HwndSource.FromHwnd(new WindowInteropHelper(dialog).Handle)?.CompositionTarget;
                        earlyTheme = !dialog.IsLoaded && dialog.Background is SolidColorBrush brush && target?.BackgroundColor == brush.Color;
                        earlyCloak = NativeStartupFrameCapture.IsAppCloaked(dialog);
                    };
                    gate!.BeforeReveal += () => fullFrame = NativeStartupFrameCapture.IsAppCloaked(dialog)
                        && dialog.ActualWidth >= dialog.MinWidth && dialog.ActualHeight >= dialog.MinHeight
                        && dialog.Content is FrameworkElement { Opacity: 1, ActualWidth: > 0, ActualHeight: > 0 };
                    try
                    {
                        if (modal)
                        {
                            var completed = new TaskCompletionSource<bool>();
                            dialog.ContentRendered += async (_, _) =>
                            {
                                try { completed.TrySetResult(await gate.Ready.WaitAsync(TimeSpan.FromSeconds(5))); }
                                catch (Exception ex) { completed.TrySetException(ex); }
                                finally { dialog.Close(); }
                            };
                            dialog.ShowDialog();
                            Check(await completed.Task, $"Modal secondary window renders and closes normally in {theme}");
                        }
                        else
                        {
                            dialog.Show();
                            Check(await gate.Ready.WaitAsync(TimeSpan.FromSeconds(5)), $"Secondary window reveals its composed first frame in {theme}");
                            Check(!NativeStartupFrameCapture.IsAppCloaked(dialog), $"Secondary window is visible after presentation in {theme}");
                            var pixels = NativeStartupFrameCapture.Capture(dialog, Path.Combine(output, $"secondary-first-frame-{theme}.png"));
                            Check(pixels[0] == ((SolidColorBrush)dialog.Background).Color, $"Secondary native first frame uses themed page color in {theme}: {pixels[0]}");
                        }
                        Check(earlyTheme && earlyCloak && fullFrame,
                            $"Secondary theme, concealment and fully opaque content verified before reveal: {theme}, modal={modal}");
                    }
                    finally { dialog.Close(); }
                }
            }
        }
        finally { app.Themes.Select(preference, save: false); }
    }
}
#endif
