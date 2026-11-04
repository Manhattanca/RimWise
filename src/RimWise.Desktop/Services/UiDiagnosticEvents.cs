using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RimWise.Integration.Diagnostics;

namespace RimWise.Desktop.Services;

/// <summary>Control identities and actions only. Never observe passwords, text entry or key presses.</summary>
internal static class UiDiagnosticEvents
{
    public static void Install(Application app)
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, e) =>
        {
            if (sender is not Window window || !ReferenceEquals(e.OriginalSource, window)) return;
            Record(window, "window-opened");
            window.Closed += (_, _) => Record(window, "window-closed");
        }));
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent,
            new RoutedEventHandler((sender, _) => Record((FrameworkElement)sender, "button-clicked")), true);
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.ClickEvent,
            new RoutedEventHandler((sender, e) => { if (ReferenceEquals(sender, e.Source)) Record((FrameworkElement)sender, "menu-clicked"); }), true);
        EventManager.RegisterClassHandler(typeof(ComboBox), Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((sender, _) =>
            {
                var control = (ComboBox)sender;
                Record(control, "selection-changed", "selectedIndex=" + control.SelectedIndex);
            }), true);
        app.DispatcherUnhandledException += (_, e) => Failure("dispatcher-failure", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Failure("process-failure", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Failure("unobserved-task-failure", e.Exception);
    }

    private static void Record(FrameworkElement control, string action, string? detail = null)
    {
        // Static captions identify unnamed controls. TextBox/PasswordBox values are never read.
        var caption = control switch { MenuItem item => item.Header as string, ButtonBase button => button.Content as string, _ => null };
        DiagnosticEventLog.Current?.Record("interface", action,
            $"window={Window.GetWindow(control)?.GetType().Name}; control={control.GetType().Name}; name={control.Name}; caption={caption}; {detail}");
    }

    private static void Failure(string operation, Exception? error)
    {
        DiagnosticEventLog.Current?.Record("application", operation, error?.ToString());
        // Preserve normal exception behavior; diagnostics never marks an exception handled.
    }
}
