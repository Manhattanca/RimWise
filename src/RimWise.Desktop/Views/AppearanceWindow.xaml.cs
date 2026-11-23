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
    private readonly AppearanceSettings original;
    private readonly AppearanceImage? originalImage;
    private AppearanceSettings baseline;
    private AppearanceTransparency transparency = new();
    private AppearanceControlOverrides controls = new();
    internal bool AdvancedRequested { get; private set; }
    private AppearanceImage? baselineImage;
    private AppearanceImage? image;
    private bool ready, importing, closed, prompting;
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
        Populate(original, activeTheme); ready = true;
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
        selectedPreset = value.Preset; selectedThemeName = name;
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
            Border = original.Border,
            Background = image?.Name, Placement = (BackgroundPlacement)PlacementBox.SelectedIndex,
            Shade = ShadeSlider.Value / 100, Blur = BlurSlider.Value, PanelOpacity = OpacitySlider.Value / 100,
            OtherUiOpacity = OtherOpacitySlider.Value / 100, Transparency = transparency, Controls = controls,
            PositionX = PositionXSlider.Value / 100, PositionY = PositionYSlider.Value / 100
        };
    }
    private void Changed(object sender, RoutedEventArgs e)
    {
        if (!ready || importing) return;
        // Selecting a new preset resets explicit color overrides, but retains the user's background.
        if (ReferenceEquals(sender, PresetBox))
        {
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
                selectedPreset = (AppearancePreset)PresetBox.SelectedIndex; selectedThemeName = null;
                ready = false; foreach (var color in ColorEditors) color.SetColor(null, false); ready = true;
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
            LocalPreview.Preview(Read(), image); SaveButton.IsEnabled = !importing;
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
    private void Reset_Click(object sender, RoutedEventArgs e) { if (importing) return; image = null; Populate(new()); Preview(); }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
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
    internal bool HasUnsavedChanges => Read() != baseline;
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
            themes.Save(Read(), image, name); baseline = Read(); baselineImage = image;
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
}
