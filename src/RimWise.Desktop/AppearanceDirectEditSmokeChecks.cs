#if DEBUG
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;
using RimWise.Integration.Marketplace;

namespace RimWise.Desktop;

internal static class AppearanceDirectEditSmokeChecks
{
    internal static async Task Run(MainWindow main, string output, Action<bool, string> check)
    {
        var themes = ((App)Application.Current).Themes;
        var baseline = themes.Current; var baselineImage = themes.Image;
        AppearanceWindow? hiddenEditor = null;
        var opener = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        opener.Tick += (_, _) =>
        {
            hiddenEditor = Application.Current.Windows.OfType<AppearanceWindow>().SingleOrDefault(window => window.Owner == main);
            if (hiddenEditor is null) return;
            opener.Stop(); hiddenEditor.AdvancedOpacityButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        opener.Start(); var session = main.EditAppearanceAsync();
        try
        {
            await Idle();
            check(hiddenEditor is { IsVisible: false } && main.IsEnabled && main.OpacityPicker is not null
                && main.OpacityEditingBar.IsVisible, "advanced entry hides the modal editor and returns to the live enabled main window");
            var tabBounds = main.MainPageTabs.TransformToAncestor(main).TransformBounds(new Rect(main.MainPageTabs.RenderSize));
            var toolBounds = main.OpacityEditingBar.TransformToAncestor(main).TransformBounds(new Rect(main.OpacityEditingBar.RenderSize));
            check(!tabBounds.IntersectsWith(toolBounds), "editing toolbar never covers navigation to other first-level pages");
            var registry = main.AppearanceControls;
            File.WriteAllText(Path.Combine(output, "registry.json"), System.Text.Json.JsonSerializer.Serialize(new { Keys = registry.Targets.Select(t => t.Key).ToArray(), SaveLoaded = main.SaveOrderButton.IsLoaded, SaveTemplated = main.SaveOrderButton.TemplatedParent?.GetType().Name, Root = Window.GetWindow(main.SaveOrderButton)?.GetType().Name }));
            var save = registry.Find("MainWindow.SaveOrderButton");
            var import = registry.Find("MainWindow.ImportOrderButton");
            var search = registry.Find("MainWindow.SearchBox");
            check(save is not null && import is not null && search is null && registry.Find("MainWindow.SearchBox") is null && registry.Find("MainWindow.FileMenu") is not null
                && registry.Find("MainWindow.ModsPageTab") is not null && registry.Find("MainWindow.AppCaption") is null,
                "search remains outside per-control opacity editing while real action buttons and top controls have identities");
            hiddenEditor!.OpacitySlider.Value = 37; hiddenEditor.OtherOpacitySlider.Value = 0; await Idle();
            check(((SolidColorBrush)main.SaveOrderButton.Background).Color.A == 255
                && main.OrderGrid.RowBackground is SolidColorBrush { Color.A: 94 }
                && main.SearchBox.Background is SolidColorBrush { Color.A: 0 },
                "other UI zero preserves opaque buttons and panel-following mod rows");
            main.SearchBox.ApplyTemplate(); main.SearchBox.Focus(); await Idle();
            var outline = (Border)main.SearchBox.Template.FindName("Chrome", main.SearchBox);
            check(outline.BorderThickness == new Thickness(1) && outline.CornerRadius == new CornerRadius(4)
                && outline.BorderBrush is SolidColorBrush { Color.A: 255 } && main.SearchBox.ActualHeight == 34,
                "transparent focused search retains a thin rounded theme outline and compact centered input");
            var searchPoint = main.SearchBox.TranslatePoint(new Point(main.SearchBox.ActualWidth / 2, main.SearchBox.ActualHeight / 2), main);
            check(main.OpacityPicker!.TargetAt(searchPoint) is null,
                "advanced opacity picker cannot select the mod search box");
            check(registry.Find("MainWindow.OpacityEditingDone") is null && registry.Find("MainWindow.CaptionClose") is null,
                "editing tools and native caption actions are excluded from opacity targets");
            var order = main.ViewModel.Rows.Select(row => row.PackageId).ToArray();
            var oldSearch = main.SearchBox.Text;
            var oldImportAlpha = ((SolidColorBrush)main.ImportOrderButton.Background).Color.A;
            await Inspect(() => main.OpacityPicker!.Inspect(save!), dialog =>
            {
                dialog.ModeBox.SelectedIndex = 1; dialog.ValueSlider.Value = 0;
            }, async dialog =>
            {
                check(((SolidColorBrush)main.SaveOrderButton.Background).Color.A == 0
                    && ((SolidColorBrush)main.ImportOrderButton.Background).Color.A == oldImportAlpha,
                    "selected save button reaches zero without changing the adjacent import button");
                check(main.SaveOrderButton.Opacity == 1, "individual opacity changes only background brushes, not text or icons");
                check(dialog.ModeBox.Background is SolidColorBrush { Color.A: 255 }
                    && dialog.ApplyButton.Background is SolidColorBrush { Color.A: 255 }
                    && ((SolidColorBrush)dialog.FindResource("InputSurfaceBrush")).Color.A == 255,
                    "inspector controls stay opaque while the selected main-window button reaches zero");
                dialog.ModeBox.IsDropDownOpen = true; await Idle();
                check(dialog.ModeBox.Items.OfType<ComboBoxItem>().All(item => item.Background is SolidColorBrush { Color.A: 255 }),
                    "inspector dropdown items also use the opaque local palette");
                dialog.ModeBox.IsDropDownOpen = false;
                PrototypeSmokeCheck.SavePreview(dialog, Path.Combine(output, "opaque-inspector-transparent-target.png"));
                PrototypeSmokeCheck.SavePreview(main, Path.Combine(output, "live-main-independent-button.png"));
                dialog.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.CompletedTask;
            });
            check(themes.Current.Controls.Get(save!.Key)?.Opacity == 0 && hiddenEditor!.HasUnsavedChanges,
                "apply commits into the existing unsaved theme draft, not personal storage");
            check(main.SearchBox.Text == oldSearch && registry.Find("MainWindow.SearchBox") is null,
                "opacity editing leaves search input content and editability alone");
            await Inspect(() => main.OpacityPicker!.Inspect(import!), dialog => { dialog.ModeBox.SelectedIndex = 2; },
                dialog => { dialog.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask; });
            check(((SolidColorBrush)main.ImportOrderButton.Background).Color.A == 255 && ((SolidColorBrush)main.SaveOrderButton.Background).Color.A == 0,
                "second button can remain opaque while the first is transparent");
            var firstPicker = main.OpacityPicker;
            var reenter = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            reenter.Tick += (_, _) =>
            {
                if (hiddenEditor?.IsVisible != true) return;
                reenter.Stop(); hiddenEditor.AdvancedOpacityButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            reenter.Start(); main.OpacityEditingDone.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(); reenter.Stop();
            check(main.OpacityPicker is not null && !ReferenceEquals(firstPicker, main.OpacityPicker)
                && themes.Current.Controls.Get(save.Key)?.Opacity == 0 && !session.IsCompleted,
                "reentering advanced mode retains the same draft and replaces the input interception session cleanly");
            // Route a real preview input pair through the main window; choosing Save must not invoke file saving.
            await Inspect(() =>
            {
                var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
                var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent };
                main.SaveOrderButton.RaiseEvent(down); main.SaveOrderButton.RaiseEvent(up);
                check(down.Handled && up.Handled, "picker consumes press and release before the original save action");
            }, _ => { }, dialog => { dialog.DialogResult = false; return Task.CompletedTask; });
            var keyEvent = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), 0, Key.Z) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            main.SearchBox.RaiseEvent(keyEvent);
            check(keyEvent.Handled && order.SequenceEqual(main.ViewModel.Rows.Select(row => row.PackageId)),
                "editing mode blocks business keyboard shortcuts and preserves the mod order");
            main.LogsPageTab.IsChecked = true; await Idle();
            File.WriteAllText(Path.Combine(output, "log-registry.json"), System.Text.Json.JsonSerializer.Serialize(registry.Targets.Select(t => new {t.Key, t.Label}).ToArray()));
            check(main.GameLogHost.IsVisible && registry.Find("GameLogPage.SearchText") is not null && registry.Find("GameLogPage.AutoLoadButton") is not null,
                "real log-page controls register lazily when navigating in edit mode");
            await CheckAllPages(main, hiddenEditor, output, check);
            main.ModsPageTab.IsChecked = true; await Idle();
            check(registry.Find(save.Key)?.Element == main.SaveOrderButton, "returning pages preserves each control identity and override");
            await Inspect(() => main.OpacityPicker!.Inspect(save), dialog => { dialog.ModeBox.SelectedIndex = 0; },
                dialog => { dialog.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask; });
            check(themes.Current.Controls.Get(save.Key) is null && !main.SaveOrderButton.Resources.Contains("ButtonAccentBrush"),
                "follow mode removes the local override and restores normal dynamic resources");
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            var closeEditor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            closeEditor.Tick += (_, _) =>
            {
                if (hiddenEditor?.IsVisible != true) return;
                closeEditor.Stop(); AppearanceSurfaceSmokeChecks.CloseDiscard(hiddenEditor);
            };
            closeEditor.Start(); main.RaiseEvent(escape);
            await session.WaitAsync(TimeSpan.FromSeconds(8)); closeEditor.Stop();
            check(escape.Handled && !main.OpacityEditingBar.IsVisible && main.OpacityPicker is null
                && themes.Current == baseline, "Escape returns to the same editor and discarding restores the full original theme");
            check(order.SequenceEqual(main.ViewModel.Rows.Select(row => row.PackageId)), "complete live-edit workflow never changes the current sorting document");
            // Persist independent values in the existing named-theme transaction and reload a separate store.
            var savedEditor = new AppearanceWindow { Owner = main }; savedEditor.Show(); await Idle();
            savedEditor.SetControlOpacity("MainWindow.SaveOrderButton", new() { Mode = SurfaceOpacityMode.Custom, Opacity = .21 });
            check(savedEditor.SaveAppearance(close: false, "真实控件主题"), "individual control settings use normal named-theme save: " + savedEditor.Feedback.Text);
            check(new AppearanceSettingsStore(UserStorage.DirectoryPath).LoadAppearance().Controls.Get("MainWindow.SaveOrderButton")?.Opacity == .21,
                "individual control opacity survives persisted theme reload");
            savedEditor.Close();
            var savedSettings = themes.Current;
            var originalBrush = new SolidColorBrush(Colors.Red);
            main.ImportOrderButton.Resources["ButtonAccentBrush"] = originalBrush;
            themes.Preview(savedSettings with { Controls = savedSettings.Controls.With(import!.Key, new() { Mode = SurfaceOpacityMode.Custom, Opacity = .32 }) }, themes.Image);
            await Idle();
            check(originalBrush.Color == Colors.Red && !ReferenceEquals(main.ImportOrderButton.Background, originalBrush),
                "independent override clones rather than mutating a preexisting local resource");
            themes.Preview(savedSettings, themes.Image); await Idle();
            check(ReferenceEquals(main.ImportOrderButton.Resources["ButtonAccentBrush"], originalBrush),
                "removing override restores the exact preexisting local resource");
            main.ImportOrderButton.Resources.Remove("ButtonAccentBrush");
            await CheckCaption(main, output, check);
        }
        finally
        {
            opener.Stop(); main.OpacityPicker?.Dispose();
            if (hiddenEditor is { IsVisible: true }) AppearanceSurfaceSmokeChecks.CloseDiscard(hiddenEditor);
            themes.Preview(baseline, baselineImage); main.ModsPageTab.IsChecked = true;
        }
    }
    private static async Task CheckAllPages(MainWindow main, AppearanceWindow editor, string output, Action<bool, string> check)
    {
        var registry = main.AppearanceControls;
        new AutomaticSortingPreferencesStore(UserStorage.DirectoryPath).AcknowledgeNotice();
        new MarketplaceNoticeStore(UserStorage.DirectoryPath).Acknowledge();
        var marketFixture = new MarketplaceManifest(1, "appearance-fixture", "1.0", "透明度编辑测试排序", "ExampleAuthor",
            "仅用于离线界面验证。", "1.7", [], null, [new("ludeon.rimworld", null)]);
        main.ConfigureMarketplacePage(new()
        {
            LoadCatalogueAsync = _ => Task.FromResult(new MarketplaceResult<MarketplaceCatalogue>(MarketplaceJson.CreateCatalogue([marketFixture]), false, null)),
            LoadManifestAsync = (_, _) => Task.FromResult(new MarketplaceResult<MarketplaceManifest>(marketFixture, false, null)),
            DownloadAsync = (_, _, _, _) => throw new InvalidOperationException("No download should run during appearance editing."),
            PublishCurrent = () => throw new InvalidOperationException("No publish should run during appearance editing."),
            OpenTrustedLink = _ => throw new InvalidOperationException("No external navigation should run during appearance editing.")
        });
        foreach (var (tab, key) in new[]
        {
            (main.LogsPageTab, "GameLogPage.AutoLoadButton"),
            (main.AiSortingPageTab, "AiSortingPanel.SettingsButton"),
            (main.MarketPageTab, "MarketplacePage.RefreshButton")
        })
        {
            tab.IsChecked = true; await Idle();
            var target = registry.Find(key);
            check(target is not null && target.Element.IsVisible, "lazy first-level page exposes a stable editable button: " + key);
            var center = target!.Element.TranslatePoint(new Point(target.Element.ActualWidth / 2, target.Element.ActualHeight / 2), main);
            var coordinateTarget = main.OpacityPicker!.TargetAt(center);
            check(coordinateTarget?.Key == key, "actual screen-position picking selects the active page button, not hidden mod controls: " + key + " actual=" + coordinateTarget?.Key);
            check(registry.Resolve(main.AvailableGrid) is null && registry.Resolve(main.SaveOrderButton) is null,
                "collapsed mod-page controls cannot resolve as edit targets while another page is active: " + key);
            var button = (Control)target.Element;
            var enabled = button.IsEnabled;
            button.SetCurrentValue(UIElement.IsEnabledProperty, false);
            check(main.OpacityPicker.TargetAt(center)?.Key == key, "disabled business buttons remain editable by position: " + key);
            button.SetCurrentValue(UIElement.IsEnabledProperty, enabled);
            check(main.OpacityEditingTitle.Text.Contains(key.StartsWith("GameLogPage", StringComparison.Ordinal) ? "游戏日志"
                : key.StartsWith("AiSortingPanel", StringComparison.Ordinal) ? "自动排序" : "排序市场", StringComparison.Ordinal),
                "editing toolbar names the active page: " + key);
            if (key.StartsWith("MarketplacePage", StringComparison.Ordinal))
            {
                foreach (var name in new[] { "PublishButton", "MyOrdersButton", "AuthorMenuButton", "DownloadButton", "UseButton" })
                {
                    var action = registry.Find("MarketplacePage." + name)!;
                    check(action.Element.IsVisible && main.OpacityPicker.TargetAt(action.Element.TranslatePoint(
                        new Point(action.Element.ActualWidth / 2, action.Element.ActualHeight / 2), main))?.Key == action.Key,
                        "populated marketplace action is directly pickable in its displayed position: " + name);
                }
            }
            await Inspect(() =>
            {
                var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
                var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent };
                target.Element.RaiseEvent(down); target.Element.RaiseEvent(up);
                check(down.Handled && up.Handled, "active-page button input opens the inspector without invoking its business action: " + key);
            }, dialog =>
            { dialog.ModeBox.SelectedIndex = 1; dialog.ValueSlider.Value = 0; }, dialog =>
            {
                check(((Control)target!.Element).Background is SolidColorBrush { Color.A: 0 }
                    && dialog.ApplyButton.Background is SolidColorBrush { Color.A: 255 },
                    "page button preview stays separate from inspector chrome: " + key);
                main.ModsPageTab.IsChecked = true;
                check(!main.OpacityPicker!.HasHighlight, "page navigation clears the previous target highlight: " + key);
                dialog.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask;
            });
            tab.IsChecked = true; await Idle();
            check(((Control)registry.Find(key)!.Element).Background is SolidColorBrush { Color.A: 0 }, "page override survives navigation: " + key);
            var listKey = key.StartsWith("GameLogPage", StringComparison.Ordinal) ? "GameLogPage.LinesList"
                : key.StartsWith("AiSortingPanel", StringComparison.Ordinal) ? "AiSortingPanel.RejectedList" : "MarketplacePage.ResultsList";
            var listTarget = registry.Find(listKey)!;
            // The empty-market placeholder is a separate overlay, not part of the list.
            var listPoint = listTarget.Element.TranslatePoint(new Point(8, 8), main);
            var listHit = main.OpacityPicker!.TargetAt(listPoint);
            check(listHit?.Key == listKey,
                "visible content list wins coordinate picking over the collapsed mod list: " + listKey + " actual=" + listHit?.Key);
            await Inspect(() => main.OpacityPicker!.Inspect(listTarget), dialog =>
            { dialog.ModeBox.SelectedIndex = 1; dialog.ValueSlider.Value = 31; }, dialog =>
            {
                check(((SolidColorBrush)listTarget.Element.FindResource("ContentSurfaceBrush")).Color.A == 79,
                    "content list and item templates receive the selected control's independent alpha: " + listKey);
                if (listKey != "MarketplacePage.ResultsList")
                    check(((ListBox)listTarget.Element).Background is SolidColorBrush { Color.A: 79 },
                        "actual content list background changes, not only unused resources: " + listKey);
                dialog.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask;
            });
            PrototypeSmokeCheck.SavePreview(main, Path.Combine(output, key.Split('.')[0] + "-active-page.png"));
            var mainAlpha = ((SolidColorBrush)main.SaveOrderButton.Background).Color.A;
            main.ModsPageTab.IsChecked = true; await Idle();
            main.OpacityPicker!.Inspect(target);
            check(!Application.Current.Windows.OfType<ControlOpacityWindow>().Any() && ((SolidColorBrush)main.SaveOrderButton.Background).Color.A == mainAlpha,
                "stale queued targets cannot reopen an inspector after leaving their page: " + key);
            tab.IsChecked = true; await Idle();
        }
        // Future pages use the same inherited host contract without a new registry branch.
        main.LogsPageTab.IsChecked = true; await Idle();
        var content = main.GameLogHost.Content;
        var futureButton = new Button { Name = "FutureAction", Content = "未来页面操作" };
        var futureContent = new StackPanel();
        futureContent.Children.Add(futureButton);
        var future = new Border { Child = futureContent };
        AppearancePage.SetScope(future, "FuturePage");
        main.GameLogHost.Content = future; await Idle();
        File.WriteAllText(Path.Combine(output, "future-page.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            Loaded = futureButton.IsLoaded, Scope = AppearancePage.GetScope(futureButton), Tooling = AppearancePage.GetIsTooling(futureButton),
            Template = futureButton.TemplatedParent?.GetType().Name, Owner = Window.GetWindow(futureButton)?.GetType().Name,
            Keys = registry.Targets.Select(t => t.Key).ToArray()
        }));
        check(registry.Find("FuturePage.FutureAction")?.Element == futureButton, "future page scope registers without page-specific registry code");
        var dynamicButton = new Button { Name = "LaterAction", Content = "后续动态操作" };
        futureContent.Children.Add(dynamicButton); await Idle();
        check(registry.Find("FuturePage.LaterAction")?.Element == dynamicButton,
            "named control inserted into an already-loaded scope still registers");
        futureContent.Children.Remove(dynamicButton); await Idle();
        check(registry.Find("FuturePage.LaterAction") is null, "dynamically removed target is released");
        main.GameLogHost.Content = content; await Idle();
        check(registry.Find("FuturePage.FutureAction") is null, "unloaded page targets and local brushes are released");
        editor.SetSurfaceOpacity(AppearanceSurface.Buttons, new() { Mode = SurfaceOpacityMode.Custom, Opacity = 0 });
        editor.SetSurfaceOpacity(AppearanceSurface.Inputs, new() { Mode = SurfaceOpacityMode.Custom, Opacity = 0 });
        editor.SetSurfaceOpacity(AppearanceSurface.Selectors, new() { Mode = SurfaceOpacityMode.Custom, Opacity = 0 }); await Idle();
        check(main.OpacityEditingDone.Background is SolidColorBrush { Color.A: 255 }
            && editor.PresetBox.Background is SolidColorBrush { Color.A: 255 }
            && ((SolidColorBrush)editor.PreviewArea.FindResource("ButtonSurfaceBrush")).Color.A == 0,
            "global transparent categories leave editing tools opaque without hiding the scoped preview");
        var naming = new ThemeNameWindow(main, "透明主题验收"); naming.Show(); await Idle();
        check(naming.NameBox.Background is SolidColorBrush { Color.A: 255 }
            && naming.ConfirmButton.Background is SolidColorBrush { Color.A: 255 }, "theme naming remains readable at zero UI opacity");
        naming.Close();
        foreach (var surface in new[] { AppearanceSurface.Buttons, AppearanceSurface.Inputs, AppearanceSurface.Selectors })
            editor.SetSurfaceOpacity(surface, new());
        main.ModsPageTab.IsChecked = true; await Idle();
        foreach (var width in new[] { 1360d, 960d })
        {
            main.Width = width; main.UpdateLayout(); await Idle();
            var left = main.AvailableGrid.TranslatePoint(new(), main); var right = main.OrderGrid.TranslatePoint(new(), main);
            check(Math.Abs(left.Y - right.Y) < 1 && Math.Abs(main.AvailableGrid.ActualHeight - main.OrderGrid.ActualHeight) < 1,
                "both list viewports align top and bottom at width " + width);
            check(main.AvailableHeader.TranslatePoint(new(), main).Y == main.OrderHeader.TranslatePoint(new(), main).Y,
                "independent list headers align at width " + width);
            var searchBounds = main.SearchBox.TransformToAncestor(main).TransformBounds(new Rect(main.SearchBox.RenderSize));
            var availDirBounds = main.AvailableSortDirectionButton.TransformToAncestor(main).TransformBounds(new Rect(main.AvailableSortDirectionButton.RenderSize));
            var orderDirBounds = main.OrderSortDirectionButton.TransformToAncestor(main).TransformBounds(new Rect(main.OrderSortDirectionButton.RenderSize));
            check(main.SearchBox.ActualWidth >= 100 && searchBounds.Right <= main.ActualWidth - 12
                && availDirBounds.Width > 0 && orderDirBounds.Width > 0 && Math.Abs(availDirBounds.Top - orderDirBounds.Top) < 1,
                "search fits on toolbar and independent sort direction controls align inside list headers at width " + width);
            PrototypeSmokeCheck.SavePreview(main, Path.Combine(output, $"aligned-lists-{width}.png"));
        }
        main.Width = 1360; await Idle();
    }
    private static async Task CheckCaption(MainWindow main, string output, Action<bool, string> check)
    {
        var chrome = WindowChrome.GetWindowChrome(main);
        check(chrome is { CaptionHeight: 30, UseAeroCaptionButtons: false } && chrome.ResizeBorderThickness.Left > 0
            && !main.AllowsTransparency && main.Opacity == 1, "image-backed caption retains native resizing without layered-window transparency");
        var themes = ((App)Application.Current).Themes;
        var settings = themes.Current; var image = themes.Image;
        try
        {
            // Reuse only an image generated by this synthetic appearance suite.
            var backdrop = AppearanceImage.Read(Path.Combine(output, "frame-synthetic-background.png"));
            themes.Preview(settings with { Background = backdrop.Name, PanelOpacity = 0,
                Controls = settings.Controls.With("MainWindow.AppCaption", new() { Mode = SurfaceOpacityMode.Custom, Opacity = 0 }) }, backdrop);
            await Idle();
            check(main.AppCaption.Background is SolidColorBrush brush && brush.Color.A == ((SolidColorBrush)main.FindResource("SurfaceBrush")).Color.A
                && main.MainBackdrop.TranslatePoint(new Point(), main.AppCaption).Y == 0,
                "caption transparency reveals the same full-window custom image, not a detached strip");
            var samples = NativeStartupFrameCapture.Capture(main, Path.Combine(output, "image-backed-caption-native.png"), [new(500, 10)]);
            var page = ((SolidColorBrush)main.FindResource("PageBrush")).Color;
            check(samples[0] != page, "native caption sample contains the custom picture rather than an opaque page fill");
            main.CaptionMaximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            check(main.WindowState == WindowState.Maximized && main.MainClient.Margin.Left > 0, "custom maximize retains work-area inset");
            main.CaptionMaximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            check(main.WindowState == WindowState.Normal && main.MainClient.Margin == new Thickness(0), "custom restore restores normal client bounds");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var point = main.CaptionMaximize.PointToScreen(new(main.CaptionMaximize.ActualWidth / 2, main.CaptionMaximize.ActualHeight / 2));
                var packed = new IntPtr(((int)point.Y << 16) | ((int)point.X & 0xffff));
                var hit = SendMessage(new WindowInteropHelper(main).Handle, 0x0084, IntPtr.Zero, packed);
                check(hit.ToInt32() == 9, "Windows 11 maximize-button hit testing exposes HTMAXBUTTON for Snap layouts");
            }
        }
        finally { themes.Preview(settings, image); }
    }
    private static async Task Inspect(Action open, Action<ControlOpacityWindow> adjust,
        Func<ControlOpacityWindow, Task> finish)
    {
        Exception? failure = null; var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += async (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<ControlOpacityWindow>().SingleOrDefault();
            if (dialog is null) return; timer.Stop();
            try { adjust(dialog); await Idle(); await finish(dialog); done.TrySetResult(); }
            catch (Exception ex) { failure = ex; dialog.Close(); done.TrySetResult(); }
        };
        timer.Start();
        try { open(); await done.Task.WaitAsync(TimeSpan.FromSeconds(8)); if (failure is not null) throw failure; }
        finally { timer.Stop(); }
    }
    private static async Task Idle()
    {
        await Task.Delay(300); await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) => { CompositionTarget.Rendering -= handler; rendered.TrySetResult(); };
        CompositionTarget.Rendering += handler;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= handler; }
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        NativeStartupFrameCapture.FlushComposition();
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
#endif

