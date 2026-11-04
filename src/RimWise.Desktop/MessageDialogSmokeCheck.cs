#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

/// <summary>Exercises real modal prompts with synthetic text; no game, network or secret access.</summary>
internal static class MessageDialogSmokeCheck
{
    internal static void Attach(MainWindow owner, string output)
    {
        owner.ContentRendered += (_, _) =>
        {
            Directory.CreateDirectory(output);
            try
            {
                Run(owner, output);
                owner.ViewModel.MarkExported();
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                owner.ViewModel.MarkExported();
                Application.Current.Shutdown(1);
            }
        };
    }

    private static void Run(MainWindow owner, string output)
    {
        var checks = new List<string>();
        var themes = ((App)Application.Current).Themes;
        foreach (var theme in new[] { ThemePreference.Dark, ThemePreference.Light })
        {
            themes.Select(theme, save: false);
            foreach (var buttons in new[] { MessageBoxButton.OK, MessageBoxButton.OKCancel,
                MessageBoxButton.YesNo, MessageBoxButton.YesNoCancel })
            {
                var choices = Choices(buttons);
                foreach (var choice in choices)
                {
                    var actual = Exercise(owner, buttons, MessageBoxImage.Question, MessageBoxResult.None, dialog =>
                    {
                        Assert(dialog.Owner == owner && dialog.ShowInTaskbar == false, "Prompt lost its owner.");
                        Assert(dialog.ButtonsPanel.Children.OfType<Button>().Select(b => (MessageBoxResult)b.Tag)
                            .SequenceEqual(choices), "Choice order changed.");
                        Assert(dialog.ButtonsPanel.Children.OfType<Button>().Single(b => b.IsDefault).Tag.Equals(choices[0]),
                            "Original default choice changed.");
                        AssertPalette(dialog);
                        if (choice == choices[0])
                            PrototypeSmokeCheck.SavePreview(dialog, Path.Combine(output, $"{theme}-{buttons}.png"));
                        Click(dialog, choice);
                    });
                    Assert(actual == choice, "Wrong modal result: " + buttons + " " + choice);
                }
                checks.Add($"{theme}: {buttons} all explicit choices and default focus");

                var dismissed = Exercise(owner, buttons, MessageBoxImage.Question, MessageBoxResult.None, dialog =>
                {
                    if (buttons == MessageBoxButton.YesNo)
                    {
                        Assert(!dialog.CloseButton.IsEnabled, "Yes/No has an ambiguous close action.");
                        Escape(dialog);
                        Assert(dialog.IsVisible && dialog.Result == MessageBoxResult.None, "Esc discarded a recovery task.");
                        dialog.Close();
                        Assert(dialog.IsVisible && dialog.Result == MessageBoxResult.None, "X/Alt+F4 discarded a recovery task.");
                        Click(dialog, MessageBoxResult.No);
                    }
                    else Escape(dialog);
                });
                Assert(dismissed == (buttons == MessageBoxButton.OK ? MessageBoxResult.OK
                    : buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel), "Unsafe dismiss result.");
                checks.Add($"{theme}: {buttons} Escape/window-close semantics");
            }

            Exercise(owner, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, dialog =>
            {
                var no = dialog.ButtonsPanel.Children.OfType<Button>().Single(b => b.Tag.Equals(MessageBoxResult.No));
                Assert(no.IsDefault && no.IsKeyboardFocused, "Recovery no longer defaults to No.");
                Click(dialog, MessageBoxResult.No);
            });
            checks.Add(theme + ": explicit recovery default remains No");

            foreach (var icon in new[] { MessageBoxImage.None, MessageBoxImage.Information, MessageBoxImage.Warning, MessageBoxImage.Error })
                Exercise(owner, MessageBoxButton.OK, icon, MessageBoxResult.None, dialog =>
                {
                    Assert((dialog.SeverityIcon.Visibility == Visibility.Collapsed) == (icon == MessageBoxImage.None), "Wrong severity icon.");
                    Assert((dialog.TriangleIcon.Visibility == Visibility.Visible) == (icon == MessageBoxImage.Warning), "Warning shape lost.");
                    PrototypeSmokeCheck.SavePreview(dialog, Path.Combine(output, $"{theme}-{icon}.png"));
                    Click(dialog, MessageBoxResult.OK);
                });
            checks.Add(theme + ": information, warning, error and icon-free layouts");

            var longText = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"{i}：无法读取 D:\\中文路径 [模组] & 文件夹\\" + new string('长', 60)));
            Exercise(owner, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, dialog =>
            {
                Assert(dialog.MessageText.Text == longText, "Long message was truncated.");
                Assert(dialog.MessageScroll.ScrollableHeight > 0, "Long text cannot scroll.");
                Assert(dialog.ActualHeight <= dialog.MaxHeight + 1 && dialog.ActualWidth <= dialog.MaxWidth + 1,
                    "Dialog extends beyond work area.");
                var cancel = dialog.ButtonsPanel.Children.OfType<Button>().Single(b => b.IsCancel);
                var bottom = cancel.TranslatePoint(new Point(0, cancel.ActualHeight), dialog);
                Assert(bottom.Y <= dialog.ActualHeight, "Footer is clipped.");
                PrototypeSmokeCheck.SavePreview(dialog, Path.Combine(output, theme + "-长文本.png"));
                themes.Select(theme == ThemePreference.Dark ? ThemePreference.Light : ThemePreference.Dark, save: false);
                dialog.UpdateLayout();
                AssertPalette(dialog);
                Click(dialog, MessageBoxResult.Cancel);
            }, longText);
            themes.Select(theme, save: false);
            checks.Add(theme + ": Unicode/long text scrolling, visible footer and open-dialog theme switch");

            // Trigger the exact unsaved-changes path shown in the user's screenshot.
            owner.LoadSample();
            owner.ViewModel.SelectedRow = owner.ViewModel.Rows[2];
            owner.ViewModel.MoveUpCommand.Execute(null);
            var before = owner.ViewModel.Rows.Select(row => row.PackageId).ToArray();
            Exception? failure = null;
            owner.Dispatcher.BeginInvoke(() =>
            {
                var dialog = Application.Current.Windows.OfType<MessageDialogWindow>().Single();
                try
                {
                    Assert(dialog.Title == "未保存的调整", "Real unsaved prompt was not replaced.");
                    PrototypeSmokeCheck.SavePreview(dialog, Path.Combine(output, theme + "-未保存的调整.png"));
                }
                catch (Exception ex) { failure = ex; }
                finally { Click(dialog, MessageBoxResult.Cancel); }
            }, DispatcherPriority.ApplicationIdle);
            owner.Close();
            if (failure is not null) throw failure;
            Assert(owner.IsVisible && owner.ViewModel.HasChanges && before.SequenceEqual(owner.ViewModel.Rows.Select(row => row.PackageId)),
                "Cancelling real close lost editing state.");
            owner.ViewModel.MarkExported();
            checks.Add(theme + ": real unsaved-order cancellation preserves window, order and dirty state");
        }

        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static MessageBoxResult Exercise(Window owner, MessageBoxButton buttons, MessageBoxImage icon,
        MessageBoxResult defaultResult, Action<MessageDialogWindow> action, string? message = null)
    {
        Exception? failure = null;
        owner.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<MessageDialogWindow>().Single();
            try { dialog.UpdateLayout(); action(dialog); }
            catch (Exception ex)
            {
                failure = ex;
                Click(dialog, Choices(buttons).Last());
            }
        }, DispatcherPriority.ApplicationIdle);
        var result = MessageDialog.Show(owner, message ?? "当前有未保存的调整。继续操作会放弃这些调整，是否继续？",
            "未保存的调整", buttons, icon, defaultResult);
        if (failure is not null) throw failure;
        return result;
    }

    private static void AssertPalette(MessageDialogWindow dialog)
    {
        Assert(dialog.DialogSurface.Background is SolidColorBrush actual
            && Application.Current.FindResource("PageBrush") is SolidColorBrush expected && actual.Color == expected.Color,
            "Dialog does not follow current theme.");
        Assert(dialog.MessageText.Foreground is SolidColorBrush text
            && Application.Current.FindResource("TextBrush") is SolidColorBrush expectedText && text.Color == expectedText.Color,
            "Dialog text does not follow current theme.");
    }

    private static MessageBoxResult[] Choices(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OK => [MessageBoxResult.OK],
        MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
        MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
        _ => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel]
    };

    private static void Click(MessageDialogWindow dialog, MessageBoxResult result) =>
        dialog.ButtonsPanel.Children.OfType<Button>().Single(b => b.Tag.Equals(result)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Escape(MessageDialogWindow dialog) => dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(dialog), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
#endif
