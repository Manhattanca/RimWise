using System.Windows;
using System.Windows.Controls;
using RimWise.Desktop.Services;
using RimWise.Integration.Updates;

namespace RimWise.Desktop.Views;

/// <summary>Displays release text as inert text, never as executable HTML or external content.</summary>
internal sealed class ReleaseNotesWindow : Window
{
    internal TextBlock NotesText { get; }
    internal Button DismissButton { get; }
    internal ReleaseNotesWindow(Window owner, InstalledReleaseNotes notes)
    {
        notes.Validate(); Owner = owner; Title = "更新日志"; Width = 620; Height = 460;
        MinWidth = 420; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AppearanceEditorChrome.Attach(this);
        var layout = new DockPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = "RimWise " + notes.Version + " 更新日志", FontSize = 21,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var source = new TextBlock { Text = "与 GitHub 此版本更新日志一致", FontSize = 12, Margin = new Thickness(0, 0, 0, 16) };
        source.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(source, Dock.Top); layout.Children.Add(source);
        DismissButton = new Button { Content = "已知晓", IsDefault = true, IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), MinWidth = 90 };
        DismissButton.Click += (_, _) => Close();
        DockPanel.SetDock(DismissButton, Dock.Bottom); layout.Children.Add(DismissButton);
        NotesText = new TextBlock { Text = notes.Body, TextWrapping = TextWrapping.Wrap, LineHeight = 25 };
        layout.Children.Add(new ScrollViewer { Content = NotesText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = layout;
    }
}
