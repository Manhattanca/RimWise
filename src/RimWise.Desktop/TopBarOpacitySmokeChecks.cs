#if DEBUG
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class TopBarOpacitySmokeChecks
{
    internal static async Task Run(MainWindow main, string output, Action<bool, string> check)
    {
        var themes = ((App)Application.Current).Themes;
        var original = themes.Current; var image = themes.Image;
        try
        {
            var menus = new[] { main.FileMenu, main.SettingsMenu, main.GameMenu, main.HelpMenu };
            var radii = new[] { new CornerRadius(6, 0, 0, 6), new CornerRadius(0), new CornerRadius(0), new CornerRadius(0, 6, 6, 0) };
            for (var i = 0; i < menus.Length; i++)
            {
                var menu = menus[i]; menu.ApplyTemplate();
                var chrome = (Border)menu.Template.FindName("TopMenuChrome", menu);
                check(chrome.CornerRadius == radii[i] && menu.Margin == new Thickness(0), "joined menu uses rounded outer ends and square contiguous internal edges: " + menu.Name);
                if (i > 0)
                {
                    var previous = menus[i - 1];
                    check(Math.Abs(previous.TranslatePoint(new Point(previous.ActualWidth, 0), main).X - menu.TranslatePoint(new Point(), main).X) < .1
                        && Math.Abs(previous.ActualHeight - menu.ActualHeight) < .1, "joined menu neighbors meet without gaps or height changes: " + menu.Name);
                }
            }
            foreach (var preset in new[] { AppearancePreset.Light, AppearancePreset.Dark })
            {
                themes.Preview(original with { Preset = preset, Controls = new() }, image); await Idle();
                check(menus.All(menu => menu.Background is SolidColorBrush { Color.A: 255 }), "joined menu has one continuous default button surface in preset " + preset);
                foreach (var menu in new[] { main.FileMenu, main.HelpMenu })
                {
                    menu.IsSubmenuOpen = true; await Idle();
                    var popup = (Popup)menu.Template.FindName("PART_Popup", menu);
                    var chrome = (Border)menu.Template.FindName("TopMenuChrome", menu);
                    check(popup.IsOpen && popup.Child is FrameworkElement { IsVisible: true, ActualHeight: > 0 }
                        && chrome.CornerRadius == radii[menu == main.FileMenu ? 0 : 3], "rounded active menu preserves a complete native submenu: " + preset + "/" + menu.Name);
                    menu.IsSubmenuOpen = false; await Idle();
                }
                PrototypeSmokeCheck.SavePreview(main, Path.Combine(output, "joined-menu-" + preset + ".png"));
            }
            var settings = original with { PanelOpacity = .37, OtherUiOpacity = 0,
                Transparency = new() { Bars = new() { Mode = SurfaceOpacityMode.Opaque } },
                Controls = new AppearanceControlOverrides().With("MainWindow.MainMenu", new() { Mode = SurfaceOpacityMode.Custom, Opacity = .25 })
                    .With("MainWindow.AppCaption", new() { Mode = SurfaceOpacityMode.Custom, Opacity = 0 }) };
            themes.Preview(settings, image); await Idle(); main.AppearanceControls.Refresh();
            check(main.MainMenu.Background is SolidColorBrush { Color.A: 94 }
                && main.AppCaption.Background is SolidColorBrush { Color.A: 94 }
                && main.MainNavigationBackground.Background is SolidColorBrush { Color.A: 94 },
                "top background follows panel opacity, ignoring legacy whole-menu and caption overrides");
            foreach (var menu in new[] { main.FileMenu, main.SettingsMenu, main.GameMenu, main.HelpMenu })
                check(main.AppearanceControls.Find("MainWindow." + menu.Name)?.Surface == AppearanceSurface.Buttons
                    && ((SolidColorBrush)menu.FindResource("TopButtonSurfaceBrush")).Color.A == 0,
                    "legacy container override does not become a hidden top-button rule: " + menu.Name);
            settings = settings with { Controls = settings.Controls.With("MainWindow.FileMenu", new() { Mode = SurfaceOpacityMode.Custom, Opacity = .5 })
                .With("MainWindow.ModsPageTab", new() { Mode = SurfaceOpacityMode.Custom, Opacity = .31 }) };
            themes.Preview(settings, image); await Idle();
            check(((SolidColorBrush)main.FileMenu.FindResource("TopButtonSurfaceBrush")).Color.A == 128
                && main.FileMenu.Background is SolidColorBrush { Color.A: 128 }
                && ((SolidColorBrush)main.HelpMenu.FindResource("TopButtonSurfaceBrush")).Color.A == 0
                && ((SolidColorBrush)main.ModsPageTab.FindResource("TopButtonSurfaceBrush")).Color.A == 79,
                "menu and page buttons have independent opacity without changing siblings");
            check(main.MainMenu.Background is SolidColorBrush { Color.A: 94 }
                && main.MainNavigationBackground.Background is SolidColorBrush { Color.A: 94 }
                && ((SolidColorBrush)main.FileMenu.FindResource("PanelBrush")).Color.A == 255,
                "individual top-button opacity leaves surrounding bar and popup content backgrounds alone");
            check(main.AppearanceControls.Find("MainWindow.MainMenu") is null
                && main.AppearanceControls.Find("MainWindow.AppCaption") is null,
                "top container blank space is no longer an opacity-edit target");
            themes.Preview(settings with { Controls = settings.Controls.With("MainWindow.FileMenu", new() { Mode = SurfaceOpacityMode.Follow }) }, image);
            await Idle();
            check(((SolidColorBrush)main.FileMenu.FindResource("TopButtonSurfaceBrush")).Color.A == 0,
                "restoring a top button to Follow really clears the local opacity despite retired legacy keys");
            check(main.FileMenu.Background is SolidColorBrush { Color.A: 255 }, "joined menu Follow restores the normal button surface");
            foreach (var alpha in new[] { 0d, 1d })
            {
                themes.Preview(settings with { Controls = settings.Controls.With("MainWindow.FileMenu", new() { Mode = SurfaceOpacityMode.Custom, Opacity = alpha }) }, image);
                await Idle();
                check(main.FileMenu.Background is SolidColorBrush brush && brush.Color.A == (byte)(alpha * 255)
                    && main.SettingsMenu.Background is SolidColorBrush { Color.A: 255 } && main.MainMenu.Background is SolidColorBrush { Color.A: 94 },
                    "joined group preserves individual zero/full button opacity without an opaque group underlay: " + alpha);
            }
        }
        finally { themes.Preview(original, image); await Idle(); }
    }
    private static async Task Idle() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
}
#endif
