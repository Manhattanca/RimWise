using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;

namespace RimWise.Desktop.Views;

/// <summary>One theme-aware entry point for application notices and confirmation prompts.</summary>
internal static class MessageDialog
{
    internal static MessageBoxResult Show(Window owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dialog = new MessageDialogWindow(owner, message, title, buttons, image, defaultResult);
        dialog.ShowDialog();
        return dialog.Result;
    }
}

public partial class MessageDialogWindow : Window
{
    private readonly MessageBoxButton buttons;
    private readonly TimeSpan confirmationDelay;
    private readonly Stopwatch confirmationWatch = new();
    private DispatcherTimer? confirmationTimer;
    internal MessageBoxResult Result { get; private set; }

    internal MessageDialogWindow(Window owner, string message, string title, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult, TimeSpan confirmationDelay = default)
    {
        if (!Enum.IsDefined(buttons)) throw new ArgumentOutOfRangeException(nameof(buttons));
        InitializeComponent();
        Owner = owner;
        Title = title;
        MessageText.Text = message;
        this.buttons = buttons;
        this.confirmationDelay = confirmationDelay;
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 32);
        MaxWidth = Math.Max(300, SystemParameters.WorkArea.Width - 32);
        MessageScroll.MaxHeight = Math.Min(380, Math.Max(80, MaxHeight - 180));
        SetIcon(image);

        var choices = buttons switch
        {
            MessageBoxButton.OK => new[] { MessageBoxResult.OK },
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            _ => throw new ArgumentOutOfRangeException(nameof(buttons))
        };
        var selectedDefault = choices.Contains(defaultResult) ? defaultResult : choices[0];
        foreach (var choice in choices)
        {
            var button = new Button
            {
                Content = choice switch { MessageBoxResult.OK => "确定", MessageBoxResult.Cancel => "取消",
                    MessageBoxResult.Yes => "是", _ => "否" },
                Tag = choice,
                IsDefault = choice == selectedDefault,
                IsCancel = confirmationDelay == default && (choice == MessageBoxResult.Cancel || buttons == MessageBoxButton.OK),
                MinHeight = 36
            };
            AutomationProperties.SetName(button, button.Content.ToString());
            if (choice is MessageBoxResult.OK or MessageBoxResult.Yes)
            {
                button.SetResourceReference(BackgroundProperty, "AccentBrush");
                button.SetResourceReference(ForegroundProperty, "AccentTextBrush");
                button.SetResourceReference(BorderBrushProperty, "AccentBrush");
            }
            button.Click += (_, _) =>
            {
                if (confirmationDelay > TimeSpan.Zero && (!confirmationWatch.IsRunning || confirmationWatch.Elapsed < confirmationDelay)) return;
                Result = choice; DialogResult = true;
            };
            ButtonsPanel.Children.Add(button);
            if (confirmationDelay > TimeSpan.Zero)
            {
                button.IsEnabled = false;
                button.Content = $"确定（{Math.Ceiling(confirmationDelay.TotalSeconds)} 秒）";
                ContentRendered += (_, _) =>
                {
                    if (confirmationWatch.IsRunning) return;
                    confirmationWatch.Start();
                    confirmationTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (_, _) =>
                    {
                        var remaining = Math.Max(0, Math.Ceiling((confirmationDelay - confirmationWatch.Elapsed).TotalSeconds));
                        button.Content = remaining > 0 ? $"确定（{remaining} 秒）" : "确定";
                        button.IsEnabled = remaining == 0;
                        if (remaining == 0) { confirmationTimer?.Stop(); button.Focus(); }
                    }, Dispatcher);
                };
            }
            if (button.IsDefault) Loaded += (_, _) => button.Focus();
        }

        // Yes/No recovery prompts require an explicit choice: treating X or Esc as No could delete a task.
        CloseButton.IsEnabled = buttons != MessageBoxButton.YesNo && confirmationDelay == default;
        if (!CloseButton.IsEnabled) CloseButton.ToolTip = confirmationDelay > TimeSpan.Zero ? "请阅读提示后点击确定" : "请选择是或否";
        Closed += (_, _) => confirmationTimer?.Stop();
    }

    private void SetIcon(MessageBoxImage image)
    {
        if (image == MessageBoxImage.None) { SeverityIcon.Visibility = Visibility.Collapsed; return; }
        var key = image switch
        {
            MessageBoxImage.Warning => "WarningIconBrush",
            MessageBoxImage.Error => "ErrorIconBrush",
            _ => "LinkBrush"
        };
        CircleIcon.SetResourceReference(Shape.StrokeProperty, key);
        IconGlyph.SetResourceReference(TextBlock.ForegroundProperty, key);
        IconGlyph.Text = image switch { MessageBoxImage.Question => "?", MessageBoxImage.Information => "i", _ => "!" };
        var label = image switch { MessageBoxImage.Warning => "警告", MessageBoxImage.Error => "错误",
            MessageBoxImage.Question => "确认", _ => "提示" };
        AutomationProperties.SetName(SeverityIcon, label);
        if (image == MessageBoxImage.Warning)
        {
            CircleIcon.Visibility = Visibility.Collapsed;
            TriangleIcon.Visibility = Visibility.Visible;
            IconGlyph.Margin = new Thickness(0, 5, 0, 0);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (buttons != MessageBoxButton.YesNo && confirmationDelay == default) Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (Result != MessageBoxResult.None) return;
        if (buttons == MessageBoxButton.YesNo || confirmationDelay > TimeSpan.Zero) { e.Cancel = true; return; }
        Result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
    }
}
