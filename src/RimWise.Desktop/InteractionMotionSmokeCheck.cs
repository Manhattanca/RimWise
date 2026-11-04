#if DEBUG
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Services;
using RimWise.Integration.Configuration;
using RimWise.Core;

namespace RimWise.Desktop;

internal static class InteractionMotionSmokeCheck
{
    internal static void Attach(MainWindow window, string output)
    {
        window.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try
            {
                var checks = new List<string>();
                void Check(bool value, string name)
                {
                    if (!value) throw new InvalidOperationException(name);
                    checks.Add(name);
                }
                window.LoadSample();
                var vm = window.ViewModel;
                var modRoot = Path.Combine(output, "拖动图标样例");
                Directory.CreateDirectory(Path.Combine(modRoot, "About"));
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(Brushes.Teal, null, new Rect(0, 0, 30, 30));
                var bitmap = new RenderTargetBitmap(30, 30, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(modRoot, "About", "Preview.png"))) encoder.Save(file);
                var installation = new ModInstallation(modRoot, "drag-icons", ModSource.Workshop, "",
                    new("拖拽封面与状态", "测试", "example.drag", ["1.7"], "", ModRequirements.Empty, false), [])
                    { CodeKind = ModCodeKind.CSharp };
                var dragRow = new LoadOrderRow("example.drag", 1);
                dragRow.SetMatch(new OrderMatch("example.drag", [installation]));
                dragRow.SetActivity(false, true);
                dragRow.SetDiagnostics([new CatalogueDiagnostic("Sample", DiagnosticSeverity.Error, "示例状态")], true);
                object[] Selection(int count) => Enumerable.Range(0, count).Select(index => index == 0 ? (object)dragRow
                    : new InstalledModRow(installation with { Metadata = installation.Metadata! with { Name = $"选中模组 {index + 1}" } }, false)).ToArray();
                foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
                {
                    ((App)Application.Current).Themes.Select(theme, save: false);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    Check(window.MainMenu.ActualHeight <= 32, "Compact menu " + theme);
                    if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && !SystemParameters.HighContrast)
                    {
                        var handle = new WindowInteropHelper(window).Handle;
                        foreach (var (attribute, key) in new[] { (35, "PanelBrush"), (36, "TextBrush"), (34, "BorderBrush") })
                        {
                            var color = ((SolidColorBrush)window.FindResource(key)).Color;
                            var value = color.R | color.G << 8 | color.B << 16;
                            var result = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
                            Check(result == 0, $"Native frame accepts {key}{theme}: result={result}");
                        }
                    }
                    var header = window.FileMenu.TranslatePoint(new Point(), window.MainMenu);
                    Check(window.FileMenu.ActualHeight > 10 && header.Y >= 0
                        && header.Y + window.FileMenu.ActualHeight <= 33, "Visible menu header " + theme);
                    window.FileMenu.IsSubmenuOpen = true;
                    await Task.Delay(240);
                    Check(window.FileMenu.Template.FindName("PART_Popup", window.FileMenu) is System.Windows.Controls.Primitives.Popup { IsOpen: true },
                        "File menu opens " + theme);
                    await VerifyPopup((Popup)window.FileMenu.Template.FindName("PART_Popup", window.FileMenu),
                        () => window.FileMenu.IsSubmenuOpen = false, () => window.FileMenu.IsSubmenuOpen = true,
                        "File menu " + theme, Check, Path.Combine(output, "popup-" + theme));
                    window.SettingsMenu.IsSubmenuOpen = true;
                    await Task.Delay(180);
                    window.PathsMenu.IsSubmenuOpen = true;
                    await Task.Delay(180);
                    await VerifyPopup((Popup)window.PathsMenu.Template.FindName("PART_Popup", window.PathsMenu),
                        () => window.PathsMenu.IsSubmenuOpen = false, () => window.PathsMenu.IsSubmenuOpen = true,
                        "Nested menu " + theme, Check);
                    window.SettingsMenu.IsSubmenuOpen = false;
                    await Task.Delay(180);
                    window.RowHeightPopup.IsOpen = true;
                    await Task.Delay(180);
                    await VerifyPopup(window.RowHeightPopup, () => window.RowHeightPopup.IsOpen = false,
                        () => window.RowHeightPopup.IsOpen = true, "Row height " + theme, Check);
                    var context = new ContextMenu { PlacementTarget = window.FileMenu };
                    context.Items.Add(new MenuItem { Header = "测试操作" });
                    context.IsOpen = true;
                    await Task.Delay(180);
                    await VerifyPopup((Popup)context.Parent, () => context.IsOpen = false,
                        () => context.IsOpen = true, "Context menu " + theme, Check);
                    var tooltip = new ToolTip { PlacementTarget = window.FileMenu, Content = "提示浮窗" };
                    tooltip.IsOpen = true;
                    await Task.Delay(180);
                    await VerifyPopup((Popup)tooltip.Parent, () => tooltip.IsOpen = false,
                        () => tooltip.IsOpen = true, "Tooltip " + theme, Check);
                    var combo = new ComboBox { Width = 160, ItemsSource = new[] { "选项一", "选项二" } };
                    window.ModInteractionOverlay.Children.Add(combo);
                    combo.ApplyTemplate();
                    combo.IsDropDownOpen = true;
                    await Task.Delay(180);
                    await VerifyPopup((Popup)combo.Template.FindName("PART_Popup", combo), () => combo.IsDropDownOpen = false,
                        () => combo.IsDropDownOpen = true, "Combo dropdown " + theme, Check);
                    window.ModInteractionOverlay.Children.Remove(combo);
                    Check(ReferenceEquals(window.SaveOrderButton.Parent, window.LaunchGameButton.Parent), "Game beside save " + theme);
                    var buttons = ((Panel)window.SaveOrderButton.Parent).Children.OfType<Button>().ToArray();
                    Check(Array.IndexOf(buttons, window.LaunchGameButton) == Array.IndexOf(buttons, window.SaveOrderButton) + 1
                        && buttons.All(button => !Equals(button.Content, "批量标记")), "Footer actions " + theme);
                    var order = vm.Rows.Select(row => row.PackageId).ToArray();
                    var origin = window.OrderGrid.TranslatePoint(new Point(50, 90), window.ModInteractionOverlay);
                    foreach (var count in new[] { 1, 2, 3, 4, 300 })
                    {
                        using (var preview = new ModDragPreview(window.ModInteractionOverlay, Selection(count),
                                   320, origin, new(90, 22), animate: true, trackPointer: false))
                        {
                            var moved = new Point(origin.X + 100, origin.Y + 30);
                            preview.Move(moved, .016);
                            Check(preview.Angle is > 0 and <= 4, "Right tilt " + theme + count);
                            Check(Canvas.GetLeft(preview.Group) == moved.X - 90, "Pointer grip " + theme + count);
                            preview.Move(origin, .016);
                            Check(preview.Angle < 0 && preview.Angle >= -4, "Left tilt " + theme + count);
                            for (var i = 0; i < 120; i++) preview.Move(origin, .016);
                            Check(Math.Abs(preview.Angle) < .01, "Settle " + theme + count);
                            preview.Move(new(-100, -100), .016);
                            Check(preview.Group.Visibility == Visibility.Hidden, "Outside window " + theme + count);
                            preview.Move(origin, .016);
                            await Task.Delay(200);
                            window.UpdateLayout();
                            Check(preview.Group.Children.Count == Math.Min(count, 4), "Bounded preview stack " + theme + count);
                            Check(Descendants(preview.Group).OfType<ModThumbnail>().Count() == Math.Min(count, 3),
                                "At most three mod covers " + theme + count);
                            Check(Descendants(preview.Group).OfType<TextBlock>().Any(item => item.Text == $"以及更多 {count - 3} 个 Mod") == (count > 3),
                                "Remaining count " + theme + count);
                            PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"drag-{theme}-{count}.png"));
                        }
                        Check(window.ModInteractionOverlay.Children.Count == 0 && order.SequenceEqual(vm.Rows.Select(row => row.PackageId)),
                            "Cancel cleans preview and preserves order " + theme + count);
                    }
                    using (var preview = new ModDragPreview(window.ModInteractionOverlay, [vm.Rows[0]], 240, origin,
                               new(40, 20), animate: false, trackPointer: false))
                    {
                        preview.Move(new(origin.X + 100, origin.Y), .016);
                        Check(preview.Angle == 0, "Reduced motion " + theme);
                    }
                    var dialogContent = new Border { Background = (Brush)window.FindResource("PanelBrush"),
                        Child = new TextBlock { Text = "弹窗动效测试", Margin = new(25) } };
                    // Verify the common animation leaves the original opacity and transform intact.
                    InterfaceMotion.Reveal(dialogContent);
                    await Task.Delay(240);
                    Check(dialogContent.Opacity == 1 && !dialogContent.HasAnimatedProperties,
                        "Animation releases property clock " + theme);
                    var dialog = new MotionTestWindow { Owner = window, Content = dialogContent,
                        Width = 260, Height = 140, ShowInTaskbar = false };
                    dialog.Show();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                    Check(!InterfaceMotion.Enabled || dialogContent.HasAnimatedProperties, "Dialog opens with animation " + theme);
                    await Task.Delay(240);
                    Check(!dialogContent.HasAnimatedProperties && dialogContent.Opacity == 1, "Dialog animation completes " + theme);
                    dialog.Close();
                    var popupContent = new Border { Width = 150, Height = 80, Background = Brushes.Teal };
                    var popup = new Popup { PlacementTarget = window.FileMenu, Child = popupContent,
                        AllowsTransparency = true, Placement = PlacementMode.Bottom };
                    window.ModInteractionOverlay.Children.Add(popup);
                    popup.IsOpen = true;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                    Check(popup.PopupAnimation == (InterfaceMotion.Enabled ? PopupAnimation.Fade : PopupAnimation.None), "Popup opens with animation " + theme);
                    await Task.Delay(240);
                    Check(!popupContent.HasAnimatedProperties && popupContent.Opacity == 1, "Popup animation completes " + theme);
                    await VerifyPopup(popup, () => popup.IsOpen = false, () => popup.IsOpen = true, "Standalone popup " + theme, Check);
                    popup.PlacementTarget = window.LaunchGameButton;
                    popup.Placement = PlacementMode.Top;
                    popup.IsOpen = true;
                    await Task.Delay(180);
                    await VerifyPopup(popup, () => popup.IsOpen = false, () => popup.IsOpen = true, "Upward popup " + theme, Check);
                    window.ModInteractionOverlay.Children.Remove(popup);
                    foreach (var height in new[] { 28d, 38d, 64d })
                    {
                        window.SetModRowHeight(height, save: false);
                        using var preview = new ModDragPreview(window.ModInteractionOverlay, Selection(300), 420,
                            origin, new(90, height / 2), animate: false, trackPointer: false, height: height);
                        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                        var thumbnail = Descendants(preview.Card).OfType<ModThumbnail>().Single();
                        await thumbnail.ImageLoading;
                        Check(((Image)thumbnail.Child).Source is not null, "Drag cover loaded " + theme + height);
                        Check(preview.Card.ActualHeight == height, "Drag matches row height " + theme + height);
                        Check(preview.Group.Children.OfType<Border>().All(item => item.ActualHeight == height),
                            "Every stacked card matches row height " + theme + height);
                        var bounds = thumbnail.TransformToAncestor(preview.Card).TransformBounds(new Rect(thumbnail.RenderSize));
                        Check(bounds.Top >= -.1 && bounds.Bottom <= height + .1, $"Drag cover fits {theme}{height}: {bounds}");
                        Check(Descendants(preview.Card).OfType<FrameworkElement>().Any(item => item.Name == "SourceGlyph" && item.IsVisible),
                            "Drag source icon visible " + theme + height);
                        Check(Descendants(preview.Card).OfType<ContentControl>().Any(item =>
                            ReferenceEquals(item.ContentTemplate, window.FindResource("ErrorIcon")) && item.IsVisible),
                            "Drag diagnostic icon visible " + theme + height);
                        PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"card-{theme}-{height}.png"));
                    }
                    window.SetModRowHeight(38, save: false);
                }
                await BatchEditingSmokeCheck.RunAsync(window, output);
                checks.Add("Existing batch selection, drag/drop, undo and keyboard regression");
                window.Width = 960;
                window.Height = 640;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var buttonEnd = window.LaunchGameButton.TranslatePoint(new Point(window.LaunchGameButton.ActualWidth, 0), (UIElement)window.Content);
                Check(buttonEnd.X <= ((FrameworkElement)window.Content).ActualWidth, "Footer fits minimum width");
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, "minimum-window.png"));
                File.WriteAllText(Path.Combine(output, "interaction-motion.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks }));
                vm.MarkExported();
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                window.ViewModel.MarkExported();
                Application.Current.Shutdown(1);
            }
        };
    }

    private sealed class MotionTestWindow : Window { }

    private static async Task VerifyPopup(Popup popup, Action close, Action open, string name, Action<bool, string> check, string? frames = null)
    {
        check(popup.PopupAnimation == (InterfaceMotion.Enabled ? PopupAnimation.Fade : PopupAnimation.None),
            name + $" shared motion: {popup.PopupAnimation}; transparent={popup.AllowsTransparency}; source={DependencyPropertyHelper.GetValueSource(popup, Popup.PopupAnimationProperty).BaseValueSource}");
        var closed = 0;
        EventHandler handler = (_, _) => closed++;
        popup.Closed += handler;
        FrameworkElement Root()
        {
            DependencyObject root = popup.Child;
            while (VisualTreeHelper.GetParent(root) is { } parent) root = parent;
            return (FrameworkElement)root;
        }
        ScaleTransform? Scale() => (Root().RenderTransform as TransformGroup)?.Children.OfType<ScaleTransform>().LastOrDefault();
        void Frame(string phase)
        {
            if (frames is null) return;
            var root = Root();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(frames + "-" + phase + ".png");
            encoder.Save(file);
        }
        Frame("expanded");
        var scaleBeforeClose = Scale()?.ScaleY ?? 1;
        close();
        check(!popup.IsOpen, name + " logical close immediate");
        if (InterfaceMotion.Enabled)
        {
            check(closed == 0 && PresentationSource.FromVisual(popup.Child) is not null, name + " exit retains visual until contraction completes");
            check(Root().Opacity == 1 && Scale() is { HasAnimatedProperties: true }, name + $" contracts with full opacity: opacity={Root().Opacity}, scale={Scale()}, attached={AnchoredPopupMotion.GetIsEnabled(popup)}");
            await Task.Delay(60);
            // A busy dispatcher may resume after the short animation has already finished.
            check(Root().Opacity == 1 && Scale() is { } shrinking && shrinking.ScaleY < scaleBeforeClose && shrinking.ScaleY >= 0,
                name + $" shrinks toward trigger: before={scaleBeforeClose}, now={Scale()?.ScaleY}, opacity={Root().Opacity}");
            Frame("contracting");
        }
        await Task.Delay(240);
        check(closed == 1 && PresentationSource.FromVisual(popup.Child) is null, name + " closes once without orphan window");
        open();
        await Task.Delay(35);
        if (InterfaceMotion.Enabled)
        {
            check(Root().Opacity == 1 && Scale() is { HasAnimatedProperties: true, ScaleY: >= 0 and <= 1 }, name + " expands with full opacity");
            var anchor = Scale()!;
            check(anchor.ScaleX == 1, name + " width stays unchanged");
            check(anchor.CenterX >= 0 && anchor.CenterX <= Root().ActualWidth
                && anchor.CenterY >= 0 && anchor.CenterY <= Root().ActualHeight, name + " origin inside actual popup bounds");
            if (name.StartsWith("Upward popup", StringComparison.Ordinal))
                check(Math.Abs(anchor.CenterY - Root().ActualHeight) < .1, name + " expands upward from bottom edge");
            Frame("expanding");
        }
        await Task.Delay(30);
        close();
        open();
        await Task.Delay(240);
        check(popup.IsOpen && PresentationSource.FromVisual(popup.Child) is not null, name + " rapid reopen stays open");
        close();
        await Task.Delay(240);
        check(!popup.IsOpen && PresentationSource.FromVisual(popup.Child) is null, name + " rapid reopen cleans up");
        popup.Closed -= handler;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
#endif
