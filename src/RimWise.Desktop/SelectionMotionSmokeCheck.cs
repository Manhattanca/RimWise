#if DEBUG
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class SelectionMotionSmokeCheck
{
    internal static void Attach(MainWindow window, string output)
    {
        window.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try
            {
                await Run(window, output);
                window.ViewModel.MarkExported();
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                Application.Current.Shutdown(1);
            }
        };
    }

    private static async Task Run(MainWindow window, string output)
    {
        var checks = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        CompositingChecks(Check);
        window.LoadSample();
        var order = window.ViewModel.Rows.Select(row => row.PackageId).ToArray();
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        foreach (var reduced in new[] { false, true })
        {
            InterfaceMotion.ReduceMotionForCheck = reduced;
            ((App)Application.Current).Themes.Select(theme, save: false);
            var label = $"{theme}/reduced={reduced}";
            var panelColor = ((SolidColorBrush)window.FindResource("PanelBrush")).Color;
            var hoverColor = ((SolidColorBrush)window.FindResource("HoverBrush")).Color;
            var selectedColor = ((SolidColorBrush)window.FindResource("SelectionBrush")).Color;
            double Distance(Color color) => Math.Pow(color.R - panelColor.R, 2) + Math.Pow(color.G - panelColor.G, 2) + Math.Pow(color.B - panelColor.B, 2);
            Check(Distance(hoverColor) < Distance(selectedColor), "Hover contrast stays gentler than selection " + label);
            await FaceChecks(window, Check, label);
            var normal = new SolidColorBrush(Color.FromArgb(70, 20, 40, 60)); normal.Freeze();
            var selected = new SolidColorBrush(Color.FromArgb(130, 120, 90, 210)); selected.Freeze();
            var probe = new Border { Width = 90, Height = 28 };
            StateBrushMotion.SetTarget(probe, normal); StateBrushMotion.SetKey(probe, false);
            window.ModInteractionOverlay.Children.Add(probe);
            await Idle(window);
            Check(ReferenceEquals(StateBrushMotion.GetDisplay(probe), normal), "First realization snaps without entrance " + label);
            StateBrushMotion.SetTarget(probe, selected); StateBrushMotion.SetKey(probe, true);
            await Idle(window);
            var first = StateBrushMotion.GetDisplay(probe) as SolidColorBrush;
            Check(first is not null && (!InterfaceMotion.Enabled || first.HasAnimatedProperties), "Selected color transitions on a private brush " + label);
            Check(ReferenceEquals(StateBrushMotion.GetTarget(probe), selected) && normal.IsFrozen && selected.IsFrozen,
                "Shared frozen theme brushes remain unchanged " + label);
            await Task.Delay(35);
            StateBrushMotion.SetTarget(probe, normal); StateBrushMotion.SetKey(probe, false);
            await Idle(window);
            Check(!first!.HasAnimatedProperties, "Reverse selection releases the previous clock " + label);
            for (var i = 0; i < 80; i++)
            {
                StateBrushMotion.SetKey(probe, i % 2 == 0);
                StateBrushMotion.SetTarget(probe, i % 2 == 0 ? selected : normal);
            }
            await Idle(window); await Task.Delay(210);
            Check(ReferenceEquals(StateBrushMotion.GetDisplay(probe), normal), "Rapid replacements settle exactly to source brush " + label);
            StateBrushMotion.SetTarget(probe, selected);
            await Idle(window);
            Check(ReferenceEquals(StateBrushMotion.GetDisplay(probe), selected), "Theme-only changes do not animate " + label);
            probe.DataContext = new object();
            StateBrushMotion.SetKey(probe, true); StateBrushMotion.SetTarget(probe, normal);
            await Idle(window);
            Check(ReferenceEquals(StateBrushMotion.GetDisplay(probe), normal), "Recycled row has no previous item's highlight " + label);
            StateBrushMotion.SetKey(probe, false); StateBrushMotion.SetTarget(probe, selected);
            await Idle(window);
            probe.Visibility = Visibility.Collapsed;
            Check(ReferenceEquals(StateBrushMotion.GetDisplay(probe), selected), "Hidden target clears animated presentation immediately " + label);
            probe.Visibility = Visibility.Visible;
            StateBrushMotion.SetKey(probe, true); StateBrushMotion.SetTarget(probe, Brushes.Transparent);
            await Idle(window); await Task.Delay(210);
            Check(ReferenceEquals(StateBrushMotion.GetDisplay(probe), Brushes.Transparent), "Zero alpha remains exactly transparent " + label);
            var gradient = new LinearGradientBrush(Colors.Teal, Colors.Purple, 0);
            StateBrushMotion.SetKey(probe, false); StateBrushMotion.SetTarget(probe, gradient);
            await Idle(window);
            Check(ReferenceEquals(StateBrushMotion.GetDisplay(probe), gradient), "Gradient custom brushes are never flattened " + label);
            window.ModInteractionOverlay.Children.Remove(probe);

            var box = new CheckBox { Content = "选中反馈验证", Width = 170, Height = 32, IsThreeState = true };
            window.ModInteractionOverlay.Children.Add(box);
            await Idle(window);
            var face = (Border)box.Template.FindName("Box", box);
            box.IsChecked = true;
            Check(box.IsChecked == true, "Checkbox state commits before animation " + label);
            await Idle(window);
            Check(ReferenceEquals(face.Background, StateBrushMotion.GetDisplay(face)), "Checkbox template uses state transition brush " + label);
            Check(!InterfaceMotion.Enabled || face.Background is SolidColorBrush { HasAnimatedProperties: true },
                "Actual checkbox accent fill starts a short transition " + label);
            await Task.Delay(210);
            Check(ReferenceEquals(face.Background, StateBrushMotion.GetTarget(face)), "Checkbox settles without retained clock " + label);
            box.IsChecked = null;
            await Idle(window); await Task.Delay(210);
            Check(box.IsChecked is null && ((Border)box.Template.FindName("Mixed", box)).Visibility == Visibility.Visible,
                "Indeterminate checkbox behavior stays native " + label);
            window.ModInteractionOverlay.Children.Remove(box);

            var option = new ListBoxItem { Content = "列表选项", Width = 160, Height = 32 };
            var choice = new ComboBoxItem { Content = "下拉选项", Width = 160, Height = 32 };
            window.ModInteractionOverlay.Children.Add(option);
            window.ModInteractionOverlay.Children.Add(choice);
            await Idle(window);
            option.IsSelected = choice.IsSelected = true;
            await Idle(window);
            var optionFace = (Border)option.Template.FindName("ItemChrome", option);
            var choiceFace = (Border)choice.Template.FindName("Chrome", choice);
            Check(ReferenceEquals(optionFace.Background, StateBrushMotion.GetDisplay(optionFace))
                && ReferenceEquals(choiceFace.Background, StateBrushMotion.GetDisplay(choiceFace)),
                "List and dropdown options use their local presentation brush " + label);
            Check(!InterfaceMotion.Enabled || optionFace.Background is SolidColorBrush { HasAnimatedProperties: true }
                && choiceFace.Background is SolidColorBrush { HasAnimatedProperties: true },
                "List and dropdown selection both animate " + label);
            await Task.Delay(210);
            Check(ReferenceEquals(optionFace.Background, StateBrushMotion.GetTarget(optionFace))
                && ReferenceEquals(choiceFace.Background, StateBrushMotion.GetTarget(choiceFace)),
                "List and dropdown settle to their exact theme endpoints " + label);
            window.ModInteractionOverlay.Children.Remove(option);
            window.ModInteractionOverlay.Children.Remove(choice);

            window.OrderGrid.SelectedIndex = 1;
            await Idle(window);
            var row = (DataGridRow)window.OrderGrid.ItemContainerGenerator.ContainerFromIndex(1);
            var cells = Descendants<DataGridCell>(row).ToArray();
            Check(row.IsSelected && cells.Any(cell => cell.IsSelected), "Actual mod selection stays immediate " + label);
            Check(cells.Where(cell => cell.IsSelected).All(cell => ReferenceEquals(cell.Background, StateBrushMotion.GetDisplay(cell))),
                "Actual mod cells use local state brushes " + label);
            Check(!InterfaceMotion.Enabled || row.BorderBrush is SolidColorBrush { HasAnimatedProperties: true }
                && cells.Any(cell => cell.Background is SolidColorBrush { HasAnimatedProperties: true }),
                "Actual mod highlight and accent edge both animate " + label);
            await Task.Delay(210);
            Check(ReferenceEquals(row.BorderBrush, StateBrushMotion.GetTarget(row))
                && cells.All(cell => ReferenceEquals(cell.Background, StateBrushMotion.GetTarget(cell))),
                "Actual row and cells settle exactly to appearance endpoints " + label);
            window.OrderGrid.SelectedIndex = 2;
            await Idle(window); await Task.Delay(210);
            Check(!row.IsSelected && ReferenceEquals(row.BorderBrush, StateBrushMotion.GetTarget(row)),
                "Previous mod selection fades out without stale border " + label);
            window.LogsPageTab.IsChecked = true;
            Check(window.LogsPageTab.IsChecked == true, "Page state changes immediately during color transition " + label);
            await Idle(window); await Task.Delay(210);
            Check(ReferenceEquals(window.LogsPageTab.Foreground, StateBrushMotion.GetTarget(window.LogsPageTab)),
                "Navigation color and sliding indicator are independent " + label);
            window.ModsPageTab.IsChecked = true;
            await Idle(window); await Task.Delay(210);
            Check(order.SequenceEqual(window.ViewModel.Rows.Select(row => row.PackageId)), "Visual selection never changes actual load order " + label);
            PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"selection-{theme}-{reduced}.png"));
            await HoverChecks(window, Check, label, output, $"{theme}-{reduced}");
        }
        InterfaceMotion.ReduceMotionForCheck = false;
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    // Synthetic WPF property transitions on actual templates, not OS mouse injection.
    // This tests trigger/binding priority without moving the user's mouse or sending commands.
    private static void Hover(FrameworkElement element, bool value, string property = "IsMouseOver")
    {
        var type = property == "IsHighlighted" ? typeof(ComboBoxItem) : typeof(UIElement);
        var key = (DependencyPropertyKey)type.GetField(property + "PropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        element.SetValue(key, value);
    }

    private static void CompositingChecks(Action<bool, string> check)
    {
        var pairs = new[] {
            (Colors.Transparent, Color.FromRgb(40, 54, 50)),
            (Color.FromArgb(0, 255, 0, 255), Color.FromArgb(110, 30, 100, 75)),
            (Color.FromArgb(65, 200, 80, 30), Color.FromArgb(190, 25, 35, 85)),
            (Color.FromArgb(0, 250, 250, 250), Color.FromArgb(0, 0, 0, 0)) };
        foreach (var (from, to) in pairs)
        foreach (var reverse in new[] { false, true })
        foreach (var background in new[] { Colors.Black, Colors.White, Color.FromRgb(28, 40, 35), Colors.Orange })
        {
            var start = reverse ? to : from; var end = reverse ? from : to;
            static double Composite(byte channel, byte alpha, byte behind) => (channel * alpha + behind * (255 - alpha)) / 255d;
            var bounded = true;
            for (var i = 0; i <= 100; i++)
            {
                var sample = StateBrushMotion.Blend(start, end, i / 100d);
                foreach (var channels in new[] { (start.R, end.R, sample.R, background.R),
                    (start.G, end.G, sample.G, background.G), (start.B, end.B, sample.B, background.B) })
                {
                    var a = Composite(channels.Item1, start.A, channels.Item4);
                    var b = Composite(channels.Item2, end.A, channels.Item4);
                    var value = Composite(channels.Item3, sample.A, channels.Item4);
                    bounded &= value >= Math.Min(a, b) - 1.1 && value <= Math.Max(a, b) + 1.1;
                }
            }
            check(bounded, $"101 premultiplied samples never overshoot composite endpoints {from}/{to}/{reverse}/{background}");
        }
    }

    private static async Task FaceChecks(MainWindow window, Action<bool, string> check, string label)
    {
        var face = new Border { Width = 100, Height = 32, Background = Brushes.Teal, IsHitTestVisible = false };
        var caller = new TranslateTransform(2, 0); face.RenderTransform = caller;
        StateFaceMotion.SetIsActive(face, false);
        window.ModInteractionOverlay.Children.Add(face); await Idle(window);
        var group = (TransformGroup)face.RenderTransform;
        var scale = (ScaleTransform)group.Children[^1];
        check(ReferenceEquals(group.Children[0], caller) && scale.ScaleX == 1,
            "Fill expansion preserves caller transform and horizontal cell seams " + label);
        StateFaceMotion.SetIsActive(face, true);
        check(!InterfaceMotion.Enabled || scale.HasAnimatedProperties, "First hover expands only its background " + label);
        await Task.Delay(35);
        var before = scale.ScaleY;
        StateFaceMotion.SetIsActive(face, false);
        check(!InterfaceMotion.Enabled || Math.Abs(scale.ScaleY - before) < .001,
            "Reversed fill expansion resumes continuously " + label);
        for (var i = 0; i < 60; i++) StateFaceMotion.SetIsActive(face, i % 2 == 0);
        StateFaceMotion.SetIsActive(face, true);
        await Task.Delay(220);
        check(scale.ScaleY == 1 && !scale.HasAnimatedProperties && face.ActualHeight == 32,
            "Rapid expansion replacements settle without layout changes or retained clocks " + label);
        StateFaceMotion.SetIsActive(face, false);
        face.DataContext = new object();
        check(!scale.HasAnimatedProperties, "Recycled fill cancels previous item's expansion " + label);
        StateFaceMotion.SetIsActive(face, true);
        face.Visibility = Visibility.Collapsed;
        check(ReferenceEquals(face.RenderTransform, caller) && !scale.HasAnimatedProperties,
            "Hidden background restores original transform " + label);
        window.ModInteractionOverlay.Children.Remove(face);

        var brushProbe = new Border { Width = 100, Height = 32 };
        StateBrushMotion.SetTarget(brushProbe, Brushes.Transparent); StateBrushMotion.SetKey(brushProbe, false);
        window.ModInteractionOverlay.Children.Add(brushProbe); await Idle(window);
        var dark = new SolidColorBrush(Color.FromRgb(40, 54, 50)); dark.Freeze();
        StateBrushMotion.SetTarget(brushProbe, dark); StateBrushMotion.SetIsHovered(brushProbe, true); await Idle(window);
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(15);
            var color = ((SolidColorBrush)StateBrushMotion.GetDisplay(brushProbe)!).Color;
            check(color.A == 0 || color.R == 40 && color.G == 54 && color.B == 50,
                "Actual intermediate fade contains no transparent-white RGB flash " + label + "/" + i);
        }
        window.ModInteractionOverlay.Children.Remove(brushProbe);
    }

    private static async Task HoverChecks(MainWindow window, Action<bool, string> check, string label, string output, string suffix)
    {
        var order = window.ViewModel.Rows.Select(row => row.PackageId).ToArray();
        var row = (DataGridRow)window.OrderGrid.ItemContainerGenerator.ContainerFromIndex(1);
        window.OrderGrid.UnselectAll();
        await Idle(window); await Task.Delay(160);
        var cells = Descendants<DataGridCell>(row).ToArray();
        Hover(row, true);
        await Idle(window);
        check(cells.All(c => StateBrushMotion.GetIsHovered(c)), "All cells observe row hover, not separate cell hover " + label);
        check(cells.All(c => c.Template.FindName("StateFace", c) is Border face && StateFaceMotion.GetIsActive(face) == true
            && !face.IsHitTestVisible && c.RenderTransform.Value.IsIdentity),
            "Actual row hover scales only background faces, never text or cell hit geometry " + label);
        check(cells.All(c => ReferenceEquals(StateBrushMotion.GetTarget(c), c.FindResource("ListHoverBrush"))),
            "Unselected mod uses shallow list hover fill " + label);
        check(!InterfaceMotion.Enabled || cells.All(c => c.Background is SolidColorBrush { HasAnimatedProperties: true }),
            "Actual mod hover fill animates without changing native selection " + label);
        check(!row.IsSelected, "Hover does not select or reorder a mod " + label);
        await Task.Delay(170);
        PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"hover-{suffix}.png"));
        row.IsSelected = true;
        await Idle(window);
        var selectedMotion = cells[0].Background;
        check(cells.All(c => ReferenceEquals(StateBrushMotion.GetTarget(c), c.FindResource("ListSelectionBrush"))),
            "Clicked selection wins over hover " + label);
        Hover(row, false); await Idle(window);
        check(!InterfaceMotion.Enabled || ReferenceEquals(cells[0].Background, selectedMotion),
            "Leaving selected row does not replace a running selection fade " + label);
        await Task.Delay(180);
        check(cells.All(c => ReferenceEquals(c.Background, c.FindResource("ListSelectionBrush"))),
            "Mouse leave retains the deeper selected fill " + label);
        PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"selected-leave-{suffix}.png"));
        var hoverColor = ((SolidColorBrush)row.FindResource("ListHoverBrush")).Color;
        var selectedColor = ((SolidColorBrush)row.FindResource("ListSelectionBrush")).Color;
        check(hoverColor != selectedColor, "Light hover and deep selection have distinct theme endpoints " + label);
        window.OrderGrid.UnselectAll();
        await Idle(window); await Task.Delay(160);
        for (var i = 0; i < 80; i++) Hover(row, i % 2 == 0);
        await Idle(window); await Task.Delay(180);
        check(cells.All(c => ReferenceEquals(c.Background, StateBrushMotion.GetTarget(c)) && !((SolidColorBrush)c.Background).HasAnimatedProperties),
            "Rapid row crossings leave no pending highlight clocks " + label);

        foreach (var control in new FrameworkElement[] {
            new ListBoxItem { Content = "浅悬停 / 深选中", Width = 180, Height = 32 },
            new ComboBoxItem { Content = "下拉反馈", Width = 180, Height = 32 },
            new CheckBox { Content = "复选反馈", Width = 180, Height = 32 } })
        {
            window.ModInteractionOverlay.Children.Add(control); await Idle(window);
            var name = control is ComboBoxItem ? "Chrome" : control is ListBoxItem ? "ItemChrome" : "Box";
            var face = (Border)((Control)control).Template.FindName(name, control);
            Hover(control, true, control is ComboBoxItem ? "IsHighlighted" : "IsMouseOver"); await Idle(window);
            var resource = control is ListBoxItem and not ComboBoxItem ? "ContentHoverBrush" : "SelectorHoverBrush";
            check(ReferenceEquals(StateBrushMotion.GetTarget(face), face.FindResource(resource)), "Option uses its semantic shallow hover: " + control.GetType().Name + " " + label);
            check(!InterfaceMotion.Enabled || face.Background is SolidColorBrush { HasAnimatedProperties: true },
                "Actual option hover starts color transition: " + control.GetType().Name + " " + label);
            if (control is ListBoxItem list) list.IsSelected = true;
            else if (control is ComboBoxItem choice) choice.IsSelected = true;
            else ((CheckBox)control).IsChecked = true;
            await Idle(window);
            var selected = StateBrushMotion.GetTarget(face);
            Hover(control, false, control is ComboBoxItem ? "IsHighlighted" : "IsMouseOver");
            await Idle(window); await Task.Delay(180);
            check(ReferenceEquals(StateBrushMotion.GetTarget(face), selected) && ReferenceEquals(face.Background, selected),
                "Option retains selected fill after leave: " + control.GetType().Name + " " + label);
            window.ModInteractionOverlay.Children.Remove(control);
        }

        var tab = window.LogsPageTab;
        var tabFace = (Border)tab.Template.FindName("Tab", tab);
        Hover(tab, true); await Idle(window);
        check(ReferenceEquals(StateBrushMotion.GetTarget(tabFace), tabFace.FindResource("ButtonHoverBrush")), "Navigation hover is shallow " + label);
        tab.IsChecked = true; await Idle(window);
        Hover(tab, false); await Idle(window); await Task.Delay(180);
        check(ReferenceEquals(tabFace.Background, tabFace.FindResource("ButtonSelectionBrush")), "Selected page keeps the deeper fill after leave " + label);
        window.ModsPageTab.IsChecked = true; await Idle(window); await Task.Delay(180);


        // An independent advanced rule can hide the list without hover reappearing opaque.
        var transparentHover = new SolidColorBrush(Color.FromArgb(0, 20, 80, 90)); transparentHover.Freeze();
        var transparentSelection = new SolidColorBrush(Color.FromArgb(0, 40, 100, 110)); transparentSelection.Freeze();
        window.OrderGrid.Resources["ListHoverBrush"] = transparentHover;
        window.OrderGrid.Resources["ListSelectionBrush"] = transparentSelection;
        Hover(row, true); await Idle(window); await Task.Delay(180);
        check(cells.All(c => c.Background is SolidColorBrush { Color.A: 0 }), "Zero-alpha advanced list hover remains transparent " + label);
        row.IsSelected = true; await Idle(window); await Task.Delay(180);
        check(cells.All(c => c.Background is SolidColorBrush { Color.A: 0 }), "Zero-alpha selected list stays transparent " + label);
        Hover(row, false); window.OrderGrid.UnselectAll();
        window.OrderGrid.Resources.Remove("ListHoverBrush"); window.OrderGrid.Resources.Remove("ListSelectionBrush");
        await Idle(window); await Task.Delay(180);
        check(order.SequenceEqual(window.ViewModel.Rows.Select(r => r.PackageId)), "Hover/selection matrices never modify saved order " + label);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static async Task Idle(FrameworkElement element) =>
        await element.Dispatcher.InvokeAsync(element.UpdateLayout, DispatcherPriority.ApplicationIdle);
}
#endif
