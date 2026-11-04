using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RimWise.Integration.Databases;

namespace RimWise.Desktop.Views;

public partial class DatabaseSourceEditor : UserControl
{
    internal event Action? ReadRequested;
    public DatabaseSourceEditor() { InitializeComponent(); }
    internal DatabaseSource Value => new(DisabledChoice.IsChecked == true ? DatabaseSourceMode.Disabled
        : WebsiteChoice.IsChecked == true ? DatabaseSourceMode.Website
        : LocalChoice.IsChecked == true ? DatabaseSourceMode.LocalFile : DatabaseSourceMode.GitHub,
        GitHubInput.Text, WebsiteInput.Text, LocalInput.Text);
    internal void SetSource(string heading, DatabaseSource source)
    {
        Heading.Text = heading; GitHubInput.Text = source.GitHub;
        WebsiteInput.Text = source.Website; LocalInput.Text = source.LocalFile;
        (source.Mode switch { DatabaseSourceMode.Disabled => DisabledChoice, DatabaseSourceMode.Website => WebsiteChoice,
            DatabaseSourceMode.LocalFile => LocalChoice, _ => GitHubChoice }).IsChecked = true;
        UpdateMode();
    }
    private void Mode_Checked(object sender, RoutedEventArgs e) => UpdateMode();
    private void UpdateMode()
    {
        if (GitHubInput is null) return;
        GitHubInput.IsEnabled = GitHubDownload.IsEnabled = GitHubChoice.IsChecked == true;
        WebsiteInput.IsEnabled = WebsiteDownload.IsEnabled = WebsiteChoice.IsChecked == true;
        LocalInput.IsEnabled = ChooseFile.IsEnabled = ReadFile.IsEnabled = LocalChoice.IsChecked == true;
    }
    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "选择数据库文件", CheckFileExists = true,
            Filter = "数据库文件 (*.json;*.zip)|*.json;*.zip|所有文件 (*.*)|*.*" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) LocalInput.Text = picker.FileName;
    }
    private void Download_Click(object sender, RoutedEventArgs e) => ReadRequested?.Invoke();
}
