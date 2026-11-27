using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RimWise.Integration.AI;
using RimWise.Desktop.Services;

namespace RimWise.Desktop.Views;

internal sealed partial class SortingGuideView
{
    private const string Endpoint = "https://relayrax.com/v1", Model = "gpt-6.1-sol";
    private Grid? shell, sorting, settings;
    private ComboBox? openList;
    private Grid? filePicker;
    private bool removedExample;
    private Grid? decision;
    private Border? modeMenu;
    private ComboBox? modeBox;
    internal Button DecisionButton(bool remove) => Part<Button>(decision!, remove ? "ConfirmButton" : "KeepButton");
    internal ComboBox? OpenList => openList;
    private static Grid Layout(string name) => (Grid)Application.LoadComponent(new Uri($"/RimWise;component/Guide/{name}.Guide.xaml", UriKind.Relative));
    private static T Part<T>(FrameworkElement view, string name) => (T)view.FindName(name);
    private void OpenReferral()
    {
        try { Process.Start(new ProcessStartInfo(AiSettingsWindow.SuggestedServiceUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Description.Text = "无法打开浏览器。注册地址：" + AiSettingsWindow.SuggestedServiceUrl; }
    }

    private void BuildScene(Scene current)
    {
        if (openList != null) { var old = openList; openList = null; old.IsDropDownOpen = false; }
        if (shell == null)
        {
            shell = Layout("MainWindow"); sorting = Layout("AiSortingWindow"); settings = Layout("AiSettingsWindow");
            AiSortingWindow.ApplyEmbeddedLayout(sorting.FindName);
            scene.Children.Add(shell);
            sorting.Margin = new Thickness(24, 12, 24, 16); Grid.SetRow(sorting, 1); Grid.SetRowSpan(sorting, 2); shell.Children.Add(sorting);
            settings.MaxWidth = 740; settings.MaxHeight = 740; Grid.SetRowSpan(settings, 3); shell.Children.Add(settings);
            foreach (var preset in AiServicePresets.All) Part<ComboBox>(settings,"ProviderBox").Items.Add(new ComboBoxItem { Content = preset.Name });
            Part<ComboBox>(settings,"ModelListBox").Items.Add(new ComboBoxItem { Content = Model });
            Part<TextBox>(settings,"ModelBox").Visibility = Visibility.Collapsed;
            Part<Button>(settings,"ReplaceKeyButton").Visibility = Visibility.Collapsed;
            Part<CheckBox>(settings,"CompleteBaseAddressBox").IsChecked = true;
            Part<RadioButton>(shell,"ModsPageTab").GroupName = Part<RadioButton>(shell,"AiSortingPageTab").GroupName = "GuidePage";
        }
        var ai = sorting!; var form = settings!;
        var orderPage = current == Scene.Order;
        var target = Steps[StepIndex].Target;
        var picking = target is "pick-file" or "open-file";
        if (picking && filePicker == null)
        {
            filePicker = (Grid)Application.LoadComponent(new Uri("/RimWise;component/Guide/ImportPicker.xaml", UriKind.Relative));
            scene.Children.Add(filePicker);
        }
        if (filePicker != null)
        {
            filePicker.Visibility = Show(picking);
            bool selected = target == "open-file";
            Part<Image>(filePicker,"SelectedImage").Visibility = Show(selected);
        }
        if (modeMenu != null) { foreground.Children.Remove(modeMenu); modeMenu = null; }
        if (decision != null) decision.Visibility = Visibility.Collapsed;
        if (target == "decision" || !Past("decision")) removedExample = false;
        bool Past(string key) => StepIndex > Array.FindIndex(Steps, step => step.Target == key);
        Visibility Show(bool yes) => yes ? Visibility.Visible : Visibility.Collapsed;
        Part<Grid>(shell,"ModEditorBody").Visibility = Part<Border>(shell,"ModEditorFooter").Visibility = Show(orderPage);
        Part<RadioButton>(shell,"ModsPageTab").IsChecked = orderPage;
        Part<RadioButton>(shell,"AiSortingPageTab").IsChecked = !orderPage;
        ai.Visibility = Show(!orderPage); form.Visibility = Show(current == Scene.Settings);
        var names = new[] { "Harmony", "Core", "装备扩展", "建筑扩展" };
        bool importedExample = Past("open-file");
        var order = importedExample ? names.Append("家具扩展").ToArray() : names;
        var rows = order.Select((name, i) => new { Name = name, Index = i + 1 }).ToArray();
        shell.DataContext = new { HasDocument = true, GameVersionLabel = "RimWorld 1.6", GameVersionPrefix = "版本", GameVersionValue = "1.6", InstalledDlcStatuses = new[] { new Presentation.DlcStatus("皇权", "皇", true, true), new Presentation.DlcStatus("文化", "文", true, true), new Presentation.DlcStatus("生物技术", "生", true, true), new Presentation.DlcStatus("异象", "异", true, true), new Presentation.DlcStatus("奥德赛", "奥", true, true) }, VisibleRows = rows, AvailableCountLabel = "0", OrderCountLabel = rows.Length.ToString(), AvailableSortDirectionLabel = "正序", OrderSortDirectionLabel = "正序", ScanActionLabel = "刷新", LastAction = importedExample ? "已导入排序" : "" };
        ai.DataContext = form.DataContext = null;
        Part<TextBlock>(ai,"ConfigurationText").Text = "AI 模型：" + (Past("save") ? Model : "未选择模型");
        modeBox = Part<ComboBox>(ai,"PermissionBox");
        modeBox.SelectedIndex = target == "full" || Past("full") ? 1 : 0;
        if (ModeLesson)
        {
            var items = new StackPanel();
            foreach (ComboBoxItem item in modeBox.Items)
            {
                var row = new ComboBoxItem { Content = item.Content, IsSelected = Equals(item.Tag, target), Focusable = false };
                var border = new Border { Child = row, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4) };
                if (row.IsSelected) border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                items.Children.Add(border);
            }
            modeMenu = Surface(items, "PanelBrush", 2); modeMenu.IsHitTestVisible = false;
            modeMenu.BorderThickness = new Thickness(1); modeMenu.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            foreground.Children.Insert(0, modeMenu);
        }
        const string received = "已收到 AI 响应，正在校验。";
        const string analysis = "调整顺序后定义缺失已消失，但装备扩展初始化仍报错，建议试排除后复测。";
        string result = removedExample ? "已排序完成：本轮测试无红字，已自动保存；原配置已备份，可撤销。" : "已保留模组并保存排序调整，可撤销；原有报错可能仍会出现。";
        var first = AiSortingWindow.FormatRound(new(1, "正在测试第 1 次排序", 0));
        var rounds = first + AiSortingWindow.FormatRound(new(1, "本轮检测到报错，正在等待 AI 分析并调整排序…", 0))
            + AiSortingWindow.FormatRound(new(1, received, 420) { ReportText = "装备扩展引用了建筑扩展的定义，建议先加载建筑扩展。" })
            + AiSortingWindow.FormatRound(new(2, "正在测试第 2 次排序", 420))
            + AiSortingWindow.FormatRound(new(2, received, 780) { ReportText = analysis });
        Part<TextBlock>(ai,"StatusText").Text = current switch {
            Scene.Progress => GameTestStatus.Describe(new(123, 18, 49152) { Phase = "main-menu", StartupReady = true }),
            Scene.Analysis => received, Scene.Result => result, _ => "点击自动排序并测试，自动补齐必要前置并循环测试。" };
        var working = current is Scene.Progress or Scene.Analysis;
        Part<TextBlock>(ai,"SortingBusyLabel").Visibility = Part<ProgressBar>(ai,"SortingProgress").Visibility = Show(working);
        var responseBox = Part<TextBox>(ai,"ResponseText");
        responseBox.Text = current switch { Scene.Progress => first, Scene.Analysis => rounds,
            Scene.Result => rounds + AiSortingWindow.FormatResult(result, analysis, 2), _ => "" };
        responseBox.ScrollToEnd();
        if (target == "decision")
        {
            if (decision == null)
            {
                decision = Layout("TrialExclusionWindow");
                Part<TextBlock>(decision,"StatusText").Text = TrialExclusionWindow.DescribeRemovalTest(false);
                Part<DataGrid>(decision,"ModsList").ItemsSource = new[] { new TrialRemovalRow(new Presentation.LoadOrderRow("装备扩展", 1), "试排除后通过") };
                foreground.Children.Add(decision);
                foreach (var remove in new[] { true, false }) DecisionButton(remove).Click += (_, _) =>
                { if (Ready && Steps[StepIndex].Target == "decision") { removedExample = remove; Advance(); } };
            }
            decision.Visibility = Visibility.Visible;
        }
        Part<ComboBox>(form,"ProviderBox").SelectedIndex = Past("provider") ? 0 : -1;
        Part<TextBlock>(form,"ServiceNote").Text = AiServicePresets.All[0].Note;
        // The optional referral is interactive on the coach, never invoked by advancing a step.
        Part<TextBlock>(form,"ServiceSuggestion").Visibility = Visibility.Collapsed;
        Part<TextBox>(form,"EndpointBox").Text = Past("endpoint") ? Endpoint : "";
        Part<TextBlock>(form,"RequestAddressText").Text = Past("endpoint") ? "实际请求地址：" + Endpoint + "/chat/completions" : "";
        Part<PasswordBox>(form,"ApiKeyBox").Password = Past("key") ? "tutorial-only" : "";
        Part<TextBlock>(form,"ApiKeyStatus").Text = Past("key") ? "新密钥待保存（保存后加密存储）" : "尚未保存此服务的密钥；本机服务可留空";
        Part<ComboBox>(form,"ModelListBox").SelectedIndex = Past("model") ? 0 : -1;
        Part<TextBlock>(form,"StatusText").Text = Past("connection") ? "连接测试成功" : "";
        anchor = target switch {
            "import" => Part<Button>(shell,"ImportOrderButton"), "sorting-tab" => Part<RadioButton>(shell,"AiSortingPageTab"),
            "pick-file" => Part<Border>(filePicker!,"SampleFile"), "open-file" => Part<Border>(filePicker!,"OpenFile"),
            "settings" => Part<Button>(ai,"SettingsButton"), "provider" => Part<ComboBox>(form,"ProviderBox"),
            "endpoint" => Part<TextBox>(form,"EndpointBox"), "key" => Part<PasswordBox>(form,"ApiKeyBox"),
            "fetch" => Part<Button>(form,"FetchModelsButton"), "model" => Part<ComboBox>(form,"ModelListBox"),
            "connection" => Part<Button>(form,"TestConnectionButton"), "save" => Part<Button>(form,"SaveButton"),
            "connection-result" => Part<Grid>(form,"ConnectionFeedbackRow"),
            "log" => Part<Border>(ai,"SortingStatusPanel"), "response" => (FrameworkElement)Part<TextBox>(ai,"ResponseText").Parent,
            "decision" => null,
            "confirm-removal" => Part<CheckBox>(ai,"ConfirmRemovalToggle"),
            "attempt-limit" => (FrameworkElement)Part<CheckBox>(ai,"AttemptLimitToggle").Parent,
            "token-limit" => (FrameworkElement)Part<CheckBox>(ai,"TokenLimitToggle").Parent,
            "full" or "insert" => Part<ComboBox>(ai,"PermissionBox"), "start" => Part<Button>(ai,"AutomaticButton"),
            "export" => Part<Button>(ai,"SaveLastLogsButton"), null => null, _ => throw new InvalidOperationException(target) };
    }

    private void Activate(FrameworkElement part, string key)
    {
        if (part is not ComboBox box) { Advance(); return; }
        if (openList != null) return;
        const int expected = 0;
        var step = StepIndex;
        openList = box;
        void Closed(object? sender, EventArgs args)
        {
            box.DropDownClosed -= Closed;
            if (openList != box) return;
            openList = null;
            if (IsVisible && StepIndex == step && box.SelectedIndex == expected) Advance();
            else if (StepIndex == step) { Description.Text = Steps[step].Body; ActiveTarget?.Focus(); }
        }
        box.DropDownClosed += Closed;
        box.Focus(); box.IsDropDownOpen = true;
    }
}
