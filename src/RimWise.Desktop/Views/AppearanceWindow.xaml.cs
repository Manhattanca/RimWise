using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using RimWise.Desktop.Services;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Views;

public partial class AppearanceWindow : Window
{
    private readonly ThemeService themes;
    internal ThemeService LocalPreview { get; }
    private readonly AppearanceSettingsStore store;
    private AppearancePreset selectedPreset;
    private string? selectedThemeName;
    private string? border;
    private readonly AppearanceSettings original;
    private readonly AppearanceImage? originalImage;
    private AppearanceSettings baseline;
    private AppearanceTransparency transparency = new();
    private AppearanceControlOverrides controls = new();
    internal bool AdvancedRequested { get; private set; }
    private AppearanceImage? baselineImage;
    private AppearanceImage? image;
    private bool ready, importing, closed, prompting, importedTheme;
    private readonly System.Windows.Threading.DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly System.Windows.Threading.DispatcherTimer syncTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private bool interacting;
    private Point? drag;
    private double startX, startY;
    public AppearanceWindow()
    {
        themes = ((App)Application.Current).Themes;
        store = themes.SettingsStore;
        original = themes.Current; originalImage = image = themes.Image;
        baseline = original; baselineImage = originalImage;
        InitializeComponent();
        AppearanceEditorChrome.Attach(this);
        LocalPreview = themes.CreatePreview(PreviewArea);
        PreviewBackdrop.Source = LocalPreview;
        LocalPreview.Changed += LocalPreviewChanged;
        string? activeTheme = null, loadNotice = themes.LoadWarning;
        try { ReloadThemes(); activeTheme = store.ActiveThemeName; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { loadNotice = "无法读取已保存主题，原配置已保留。"; }
        foreach (var color in ColorEditors)
        {
            color.ColorChanged += (_, _) => Changed(color, new RoutedEventArgs());
            color.Expanded += (_, _) => { foreach (var other in ColorEditors) if (other != color) other.IsExpanded = false; };
        }
        Populate(original, activeTheme);
        InitializeRowHeightControls();
        Loaded += (_, _) => InitializeRowHeightControls();
        ready = true;
        Feedback.Text = loadNotice ?? "外观调整仅在本机保存，不上传背景图片。";
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); Preview(sync: false); };
        syncTimer.Tick += (_, _) => { syncTimer.Stop(); if (!interacting) Synchronize(); };
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => BeginInteraction()), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, _) => EndInteraction()), true);
        AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => BeginInteraction()), true);
        AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => EndInteraction()), true);
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) =>
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            { if (!closed && interacting && (Mouse.LeftButton != MouseButtonState.Pressed || !IsMouseCaptureWithin)) EndInteraction(); }));
        }), true);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) BeginInteraction();
        };
        PreviewKeyUp += (_, _) => EndInteraction();
        Deactivated += (_, _) => { if (interacting) EndInteraction(); };
        Closed += (_, _) =>
        {
            closed = true; previewTimer.Stop(); syncTimer.Stop();
            LocalPreview.Changed -= LocalPreviewChanged; LocalPreview.Dispose();
            themes.Preview(baseline, baselineImage);
        };
    }
    private void ReloadThemes()
    {
        while (PresetBox.Items.Count > 7) PresetBox.Items.RemoveAt(7);
        foreach (var theme in store.LoadThemes()) PresetBox.Items.Add(new ComboBoxItem { Content = theme.Name, Tag = theme });
    }
    private void Populate(AppearanceSettings value, string? name = null)
    {
        var previous = ready; ready = false;
        selectedPreset = value.Preset; selectedThemeName = name; border = value.Border;
        PresetBox.SelectedIndex = (int)value.Preset;
        if (name is not null)
            foreach (var item in PresetBox.Items.OfType<ComboBoxItem>())
                if (item.Tag is NamedAppearanceTheme theme && theme.Name == name) { PresetBox.SelectedItem = item; break; }
        AccentColor.SetColor(value.Accent, false); PageColor.SetColor(value.Page, false); PanelColor.SetColor(value.Panel, false);
        TextColor.SetColor(value.Text, false);
        PlacementBox.SelectedIndex = (int)value.Placement; ShadeSlider.Value = value.Shade * 100;
        BlurSlider.Value = value.Blur; OpacitySlider.Value = value.PanelOpacity * 100;
        OtherOpacitySlider.Value = value.OtherUiOpacity * 100;
        transparency = value.Transparency; controls = value.Controls;
        PositionXSlider.Value = value.PositionX * 100; PositionYSlider.Value = value.PositionY * 100;
        ImageStatus.Text = image is null ? "尚未导入图片 · PNG / JPG · 不超过 20 MB" : "已载入背景图片 · 保存后不依赖原文件";
        ImageAccentButton.IsEnabled = image is not null; UpdateImageControls(); ready = previous;
    }
    private void UpdateImageControls()
    {
        PageColor.IsEnabled = image is null;
        if (image is not null) PageColor.IsExpanded = false;
        PageColorNotice.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private AppearanceSettings Read()
    {
        return new()
        {
            Preset = selectedPreset, Accent = AccentColor.SelectedColor,
            Page = PageColor.SelectedColor, Panel = PanelColor.SelectedColor, Text = TextColor.SelectedColor,
            Border = border,
            Background = image?.Name, Placement = (BackgroundPlacement)PlacementBox.SelectedIndex,
            Shade = ShadeSlider.Value / 100, Blur = BlurSlider.Value, PanelOpacity = OpacitySlider.Value / 100,
            OtherUiOpacity = OtherOpacitySlider.Value / 100, Transparency = transparency, Controls = controls,
            PositionX = PositionXSlider.Value / 100, PositionY = PositionYSlider.Value / 100
        };
    }
    private void Changed(object sender, RoutedEventArgs e)
    {
        if (!ready || importing) return;
        // A preset is a complete appearance, not a palette layered on the prior theme.
        if (ReferenceEquals(sender, PresetBox))
        {
            importedTheme = false;
            if (PresetBox.SelectedItem is ComboBoxItem { Tag: NamedAppearanceTheme theme })
            {
                try
                {
                    var next = theme.Settings.Background is { } file ? AppearanceImage.Load(UserStorage.DirectoryPath, file) : null;
                    image = next; Populate(theme.Settings, theme.Name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or FormatException or NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                { image = null; Populate(theme.Settings with { Background = null }, theme.Name); Feedback.Text = "主题背景无法读取，已使用主题底色。"; }
            }
            else
            {
                var preset = (AppearancePreset)PresetBox.SelectedIndex;
                image = null; Populate(new AppearanceSettings { Preset = preset });
            }
        }
        if (!previewTimer.IsEnabled) previewTimer.Start();
        syncTimer.Stop(); if (!interacting) syncTimer.Start();
    }
    private bool Preview(bool sync = true)
    {
        previewTimer.Stop();
        try
        {
            var value = Read();
            // Pure selection uses the stored definition, including exact fractional
            // values; the color editor/percent sliders may only change formatting.
            LocalPreview.Preview(SameSettings(value, SelectedDefinition) ? SelectedDefinition : value, image);
            SaveButton.IsEnabled = !importing;
            if (sync && !interacting) Synchronize();
            foreach (var color in ColorEditors) color.RefreshColor();
            Feedback.Text = "拖动时仅更新右侧预览，松手后同步其他窗口；点击保存才会保存，取消可还原。";
            return true;
        }
        catch (InvalidDataException ex) { Feedback.Text = ex.Message; SaveButton.IsEnabled = false; return false; }
    }
    internal AppearanceColorEditor[] ColorEditors => [AccentColor, PageColor, PanelColor, TextColor];
    internal void BeginInteraction() { if (closed || !ready) return; interacting = true; syncTimer.Stop(); }
    internal void EndInteraction()
    {
        if (closed || !ready) return;
        interacting = false; syncTimer.Stop();
        if (!importing && Preview(sync: false)) Synchronize();
    }
    private void Synchronize() { if (!closed && !interacting) { syncTimer.Stop(); themes.PreviewFrom(LocalPreview); } }
    private void LocalPreviewChanged(object? sender, EventArgs e)
    {
        if (!closed && !interacting && !syncTimer.IsEnabled && !previewTimer.IsEnabled) Synchronize();
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "静态图片（PNG / JPG）|*.png;*.jpg;*.jpeg", Title = "导入背景图片", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        await ImportAsync(dialog.FileName);
    }
    internal async Task ImportAsync(string path)
    {
        if (importing) return;
        importing = true; SaveButton.IsEnabled = false; Feedback.Text = "正在处理背景图片…";
        try
        {
            var loaded = await Task.Run(() => AppearanceImage.Read(path));
            if (closed) return;
            image = loaded; ImageStatus.Text = "已载入背景图片 · 保存后不依赖原文件"; ImageAccentButton.IsEnabled = true;
            UpdateImageControls(); importing = false; Preview();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { if (!closed) Feedback.Text = ex is InvalidDataException ? ex.Message : "无法导入背景图片，请使用有效的 PNG / JPG 静态图片。"; }
        finally
        {
            importing = false;
            if (!closed)
            {
                try { Read().Validate(); SaveButton.IsEnabled = true; }
                catch (InvalidDataException) { SaveButton.IsEnabled = false; }
            }
        }
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    { if (importing) return; image = null; UpdateImageControls(); ImageAccentButton.IsEnabled = false; ImageStatus.Text = "尚未导入图片 · PNG / JPG · 不超过 20 MB"; Preview(); }
    private void ImageAccent_Click(object sender, RoutedEventArgs e) { if (image is not null) AccentColor.SelectedColor = image.Accent; }
    private void Reset_Click(object sender, RoutedEventArgs e) { if (importing) return; importedTheme = false; image = null; Populate(new()); Preview(); }
    private async void ExportTheme_Click(object sender, RoutedEventArgs e)
    {
        if (importing) return;
        var name = selectedThemeName ?? "自定义主题";
        var fileName = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        var dialog = new SaveFileDialog { Filter = "RimWise 主题文件|*.rimwise-theme", DefaultExt = AppearanceThemePackage.Extension,
            AddExtension = true, FileName = fileName + AppearanceThemePackage.Extension, Title = "导出主题", OverwritePrompt = true };
        if (dialog.ShowDialog(this) == true) await ExportThemeAsync(dialog.FileName, name);
    }
    private async void ImportTheme_Click(object sender, RoutedEventArgs e)
    {
        if (importing) return;
        var dialog = new OpenFileDialog { Filter = "RimWise 主题文件|*.rimwise-theme", Title = "导入主题", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await ImportThemeAsync(dialog.FileName);
    }
    internal async Task<bool> ExportThemeAsync(string path, string? name = null)
    {
        if (importing) return false;
        EndInteraction();
        var settings = Read(); var picture = image;
        SetTransferBusy(true, "正在导出主题及背景图片…");
        try
        {
            var themeName = name ?? selectedThemeName ?? "自定义主题";
            await Task.Run(() => AppearanceThemePackage.Write(path, themeName, settings, picture));
            Feedback.Text = picture is null ? "主题已导出。" : "主题已导出，已包含背景图片，无需另外复制图片。";
            return true;
        }
        catch (Exception ex) when (IsThemeTransferError(ex))
        { Feedback.Text = "无法导出主题，原文件和当前设置已保留。"; return false; }
        finally { SetTransferBusy(false); }
    }
    internal async Task<bool> ImportThemeAsync(string path)
    {
        if (importing) return false;
        EndInteraction(); SetTransferBusy(true, "正在读取并校验主题…");
        try
        {
            var imported = await Task.Run(() => AppearanceThemePackage.Read(path));
            if (closed) return false;
            image = imported.Image; Populate(imported.Settings, imported.Name); importedTheme = true;
            importing = false; Preview();
            Feedback.Text = "主题已导入预览，点击保存后加入预设；背景图片将保存到用户数据文件夹。";
            return true;
        }
        catch (Exception ex) when (IsThemeTransferError(ex))
        { Feedback.Text = "无法导入主题，文件可能损坏、格式不受支持或内容超限；当前外观未更改。"; return false; }
        finally { SetTransferBusy(false); }
    }
    private void SetTransferBusy(bool busy, string? message = null)
    {
        importing = busy;
        if (closed) return;
        // Freeze editable controls as well as file actions while the captured snapshot is processed off-thread.
        EditorBody.IsEnabled = !busy;
        FooterActions.IsEnabled = !busy; SaveButton.IsEnabled = !busy;
        if (message is not null) Feedback.Text = message;
    }
    private static bool IsThemeTransferError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or FormatException or InvalidOperationException
        or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException;
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (CanActivateSelectedTheme) { ActivateSelectedTheme(close: true); return; }
        var name = ThemeNameWindow.Prompt(this, selectedThemeName);
        if (name is not null) SaveAppearance(close: true, name);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void AdvancedOpacity_Click(object sender, RoutedEventArgs e)
    {
        if (importing || Owner is not MainWindow) return;
        EndInteraction(); AdvancedRequested = true; Hide();
    }
    internal void ResumeAdvancedEditing() { AdvancedRequested = false; }
    internal void SetControlOpacity(string key, SurfaceOpacitySetting rule)
    { controls = controls.With(key, rule); Preview(); }
    internal void SetSurfaceOpacity(AppearanceSurface surface, SurfaceOpacitySetting rule)
    { transparency = transparency.With(surface, rule); Preview(); }
    private AppearanceSettings SelectedDefinition => PresetBox.SelectedItem is ComboBoxItem { Tag: NamedAppearanceTheme theme }
        ? theme.Settings : new() { Preset = selectedPreset };
    internal bool CanActivateSelectedTheme => !importedTheme && SameSettings(Read(), SelectedDefinition);
    internal bool HasUnsavedChanges => importedTheme || !CanActivateSelectedTheme && !SameSettings(Read(), baseline);
    private static bool SameSettings(AppearanceSettings left, AppearanceSettings right)
    {
        // Sliders display percentages; multiplying/dividing must not turn a pure
        // selection into an edit because of an invisible floating-point roundoff.
        double Snap(double value, double expected) => Math.Abs(value - expected) < 1e-9 ? expected : value;
        string? Color(string? value, string? expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase) ? expected : value;
        return left with
        {
            Accent = Color(left.Accent, right.Accent), Page = Color(left.Page, right.Page),
            Panel = Color(left.Panel, right.Panel), Text = Color(left.Text, right.Text),
            PositionX = Snap(left.PositionX, right.PositionX), PositionY = Snap(left.PositionY, right.PositionY),
            Shade = Snap(left.Shade, right.Shade), Blur = Snap(left.Blur, right.Blur),
            PanelOpacity = Snap(left.PanelOpacity, right.PanelOpacity), OtherUiOpacity = Snap(left.OtherUiOpacity, right.OtherUiOpacity)
        } == right;
    }

    private bool ActivateSelectedTheme(bool close)
    {
        if (importing || !CanActivateSelectedTheme || !Preview()) return false;
        try
        {
            var definition = SelectedDefinition;
            var name = PresetBox.SelectedItem is ComboBoxItem { Tag: NamedAppearanceTheme theme } ? theme.Name : null;
            themes.ActivateTheme(definition, image, name);
            baseline = definition; baselineImage = image;
            Populate(definition, name);
            if (close) DialogResult = true;
            else Feedback.Text = "已使用所选主题，未修改主题内容。";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { Feedback.Text = "无法保存外观设置：" + ex.Message; return false; }
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || closed) return;
        if (prompting || importing) { e.Cancel = true; return; }
        if (!ready || !HasUnsavedChanges) return;
        // Cancel first: neither X/Esc nor a failed/cancelled naming step can discard edits.
        e.Cancel = true; prompting = true; EndInteraction();
        try
        {
            var dialog = new MessageDialogWindow(this, "外观已修改，是否保存为主题？", "保存主题",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel,
                yesText: "保存", noText: "不保存");
            dialog.ShowDialog();
            if (dialog.Result == MessageBoxResult.No) e.Cancel = false;
            else if (dialog.Result == MessageBoxResult.Yes)
            {
                var name = ThemeNameWindow.Prompt(this, selectedThemeName);
                if (name is not null && SaveAppearance(close: false, name)) e.Cancel = false;
            }
        }
        finally { prompting = false; }
    }
    internal bool SaveAppearance(bool close, string name)
    {
        if (importing || !Preview()) return false;
        try
        {
            themes.Save(Read(), image, name); baseline = Read(); baselineImage = image; importedTheme = false;
            ready = false; ReloadThemes(); Populate(Read(), name); ready = true;
            if (close) DialogResult = true;
            else Feedback.Text = "外观已保存。";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { Feedback.Text = "无法保存外观设置：" + ex.Message; return false; }
    }
    private void Preview_Down(object sender, MouseButtonEventArgs e)
    {
        if (image is null || importing) return;
        // Preview controls keep their hit testing; only empty space drags the image.
        for (DependencyObject? source = e.OriginalSource as DependencyObject; source is not null && source != PreviewArea;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
            if (source is ButtonBase or Slider or System.Windows.Controls.Primitives.Selector or TextBox or ScrollBar) return;
        drag = e.GetPosition(PreviewArea); startX = PositionXSlider.Value; startY = PositionYSlider.Value;
        PreviewArea.CaptureMouse(); e.Handled = true;
    }
    private void Preview_Move(object sender, MouseEventArgs e)
    {
        if (drag is not { } point || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(PreviewArea);
        ready = false;
        PositionXSlider.Value = Math.Clamp(startX - (current.X - point.X) / Math.Max(1, PreviewArea.ActualWidth) * 150, 0, 100);
        PositionYSlider.Value = Math.Clamp(startY - (current.Y - point.Y) / Math.Max(1, PreviewArea.ActualHeight) * 150, 0, 100);
        ready = true; if (!previewTimer.IsEnabled) previewTimer.Start();
    }
    private void Preview_Up(object sender, MouseButtonEventArgs e) { drag = null; PreviewArea.ReleaseMouseCapture(); }
    private bool synchronizingRowHeight;

    private void InitializeRowHeightControls()
    {
        var current = Owner is MainWindow main
            ? main.CurrentModRowHeight
            : Application.Current.Resources["ModRowHeight"] is double d ? d : 35;
        SyncRowHeightControls(current);
    }

    private void SyncRowHeightControls(double height)
    {
        height = Math.Clamp(Math.Round(height), 28, 64);
        var previous = synchronizingRowHeight;
        synchronizingRowHeight = true;
        try
        {
            RowHeightSlider.Value = height;
            var text = height.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            if (RowHeightInput.Text != text) RowHeightInput.Text = text;
        }
        finally { synchronizingRowHeight = previous; }
    }

    private void ApplyEditorRowHeight(double height, bool save = true)
    {
        if (!double.IsFinite(height)) return;
        height = Math.Clamp(Math.Round(height), 28, 64);
        SyncRowHeightControls(height);
        if (Owner is MainWindow main)
        {
            main.SetModRowHeight(height, save);
            return;
        }
        Application.Current.Resources["ModRowHeight"] = height;
        Application.Current.Resources["ModThumbnailSize"] = Math.Min(30, height - 4);
        if (save)
        {
            try
            {
                var listStore = new ListDisplaySettingsStore(UserStorage.DirectoryPath);
                listStore.Save(listStore.Load() with { RowHeight = height });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { }
        }
    }

    private void RowHeight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || synchronizingRowHeight) return;
        ApplyEditorRowHeight(e.NewValue, save: true);
    }

    private void RowHeightInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (!ready || synchronizingRowHeight) return;
        if (int.TryParse(RowHeightInput.Text, out var height) && height is >= 28 and <= 64)
            ApplyEditorRowHeight(height, save: true);
    }

    private void CommitEditorRowHeightInput()
    {
        var current = int.TryParse(RowHeightInput.Text, out var value) ? value : RowHeightSlider.Value;
        ApplyEditorRowHeight(current, save: true);
    }

    private void RowHeightInput_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitEditorRowHeightInput();

    private void RowHeightInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Enter) CommitEditorRowHeightInput();
            else SyncRowHeightControls(RowHeightSlider.Value);
            RowHeightSlider.Focus();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            var current = int.TryParse(RowHeightInput.Text, out var value) ? value : RowHeightSlider.Value;
            ApplyEditorRowHeight(current + (e.Key == Key.Up ? 1 : -1), save: true);
            e.Handled = true;
        }
    }

    private void RowHeightReset_Click(object sender, RoutedEventArgs e) => ApplyEditorRowHeight(35, save: true);
}