using System.Windows;
using RimWise.Desktop.Views;
using RimWise.Integration.Databases;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private async Task LoadCommunityDatabaseAsync()
    {
        try
        {
            var loaded = await Task.Run(() => new DatabaseStore(UserStorage.DirectoryPath).Load(), lifetime.Token);
            if (!lifetime.IsCancellationRequested) ViewModel.SetCommunityDatabase(loaded.Snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (DatabaseStore.Recoverable(ex)) { ViewModel.Status = "社区数据库未载入，基础排序仍可使用"; }
    }
    private async void CommunityDatabase_Click(object sender, RoutedEventArgs e)
    {
        new DatabaseWindow(UserStorage.DirectoryPath) { Owner = this }.ShowDialog();
        await LoadCommunityDatabaseAsync();
    }
}
