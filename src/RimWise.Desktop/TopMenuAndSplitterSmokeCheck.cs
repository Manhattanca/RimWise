#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class TopMenuAndSplitterSmokeCheck
{
    internal static void Attach(MainWindow main, string output)
    {
        main.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            var checks = new List<string>();
            void Check(bool value, string text)
            {
                if (!value) throw new InvalidOperationException(text);
                checks.Add(text);
            }

            var themes = ((App)Application.Current).Themes;
            var original = themes.Current;
            try
            {
                await Idle();
                var menu = main.FileMenu;
                menu.ApplyTemplate();
                var chrome = (Border)menu.Template.FindName("TopMenuChrome", menu);
                await Idle();
                var content = (FrameworkElement)menu.Template.FindName("TopMenuContent", menu);
                var translate = FindTranslate(content.RenderTransform);
                var hoverFace = (Border)menu.Template.FindName("TopMenuHoverFace", menu);
                Check(TopMenuMotion.GetEnabled(menu) && translate is not null
                    && !hoverFace.IsHitTestVisible && hoverFace.BorderBrush is SolidColorBrush
                    && hoverFace.Child is Grid hoverLayer && hoverLayer.Children.OfType<Border>().Any(line => line.Height == 2),
                    "top menu has an input-transparent accent outline and underline layer");

                translate = FindTranslate(content.RenderTransform)!;
                var reduceMotion = InterfaceMotion.ReduceMotionForCheck;
                var originalControls = themes.Current.Controls;
                var zeroButtonOpacity = originalControls.With("MainWindow.FileMenu", new() { Mode = SurfaceOpacityMode.Custom, Opacity = 0 });
                themes.Preview(themes.Current with { Controls = zeroButtonOpacity }, themes.Image);
                await Idle();
                InterfaceMotion.ReduceMotionForCheck = true;
                TopMenuMotion.SetHoveredForCheck(menu, true);
                Check(menu.Background is SolidColorBrush { Color.A: 0 } && hoverFace.Opacity == 1 && hoverFace.BorderBrush is SolidColorBrush { Color.A: 255 } && hoverFace.Child is Grid zeroOverlay && zeroOverlay.Children.OfType<Border>().All(b => b.Background is SolidColorBrush { Color.A: 255 }),
                    "top menu hover remains clearly indicated even when its surface opacity is zero");
                Check(Math.Abs(translate.X - 2.0) < .05,
                    "top menu hover matches the mod-row 2 DIP content slide");
                TopMenuMotion.SetHoveredForCheck(menu, false);
                Check(Math.Abs(translate.X) < .05 && hoverFace.Opacity == 0,
                    "top menu restores content and accent face when hover ends");
                themes.Preview(original, themes.Image); await Idle();
                InterfaceMotion.ReduceMotionForCheck = reduceMotion;
                var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = menu };
                menu.RaiseEvent(down);
                await Task.Delay(90);
                Check(translate.X > .05,
                    "top menu gives a restrained translated response while pressed");
                var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseUpEvent, Source = menu };
                menu.RaiseEvent(up);
                await Task.Delay(160);
                Check(Math.Abs(translate.X) < .05,
                    "top menu smoothly returns to its resting position after release");

                menu.IsSubmenuOpen = true;
                await Idle();
                Check(ReferenceEquals(StateBrushMotion.GetTarget(chrome), menu.FindResource("ButtonSelectionBrush")),
                    "top menu smoothly switches to its active fill while its submenu is open");
                menu.IsSubmenuOpen = false;

                var oldControls = themes.Current.Controls;
                var noSearchOverride = oldControls.With("MainWindow.SearchBox", new() { Mode = SurfaceOpacityMode.Custom, Opacity = 0 });
                themes.Preview(original with { OtherUiOpacity = .42, Controls = noSearchOverride }, themes.Image);
                await Idle();
                main.AppearanceControls.Refresh();
                Check(main.AppearanceControls.Find("MainWindow.SearchBox") is null
                    && main.SearchBox.Background is SolidColorBrush { Color.A: 107 },
                    "search ignores per-control opacity rules while retaining the shared other-UI opacity");

                foreach (var splitter in new[] { main.DetailsSplitter, main.OrderSplitter })
                {
                    splitter.ApplyTemplate();
                    var rail = (Border)splitter.Template.FindName("SplitterHitArea", splitter);
                    var grip = (Border)splitter.Template.FindName("DragGrip", splitter);
                    var face = (Border)splitter.Template.FindName("DragGripFace", splitter);
                    Check(rail.Background is null && grip.IsHitTestVisible && grip.Width == 6 && grip.Height == 24
                        && grip.Background is SolidColorBrush { Color.A: 0 }
                        && face is { Width: 3, Height: 24, IsHitTestVisible: false },
                        "splitter rail is inert outside the compact central drag grip: " + splitter.Name);
                }

                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks }));
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                themes.Preview(original, themes.Image);
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                Application.Current.Shutdown(1);
            }
            finally { themes.Preview(original, themes.Image); }
        };
    }

    private static TranslateTransform? FindTranslate(Transform transform) => transform switch
    {
        TranslateTransform translate => translate,
        TransformGroup group => group.Children.OfType<TranslateTransform>().FirstOrDefault(),
        _ => null
    };

    private static async Task Idle() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
}
#endif




