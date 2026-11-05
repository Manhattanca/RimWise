#if DEBUG
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using RimWise.Desktop.Presentation;
using RimWise.Integration.AI;
using RimWise.Integration.Diagnostics;
using RimWise.Core;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

internal static class DiagnosticExportSmokeCheck
{
    internal static void Attach(MainWindow owner, string output, bool focusedDiagnostics = false)
    {
        owner.ContentRendered += async (_, _) =>
        {
            try
            {
                await Run(owner, output, focusedDiagnostics);
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

    private static async Task Run(MainWindow owner, string output, bool focusedDiagnostics)
    {
        Directory.CreateDirectory(output);
        if (focusedDiagnostics)
        {
            owner.LoadSample();
            var mods = owner.ViewModel.Rows.Select((row, index) => new ModInstallation(
                Path.Combine(output, "fixtures", row.PackageId), row.PackageId, ModSource.Local, "",
                new(row.PackageId, "", row.PackageId, [index == 0 ? "1.6" : "1.7"], "",
                    index == 1 ? new([new("fixture.absent", "Missing dependency")], [], [], []) : ModRequirements.Empty, false), [])).ToArray();
            owner.ViewModel.ApplyScan(new(new(null, "", ""), new(1, 7), new(mods), []), observeActivity: false);
            await DiagnosticsSmokeCheck.RunAsync(owner, output);
        }
        else await AutomaticSortingPreferencesSmokeCheck.Run(owner, output);
        Assert(Descendants(owner).OfType<MenuItem>().Any(m => (m.Header as string) == "导出诊断日志…"), "Settings export entry missing");
        const string secret = "SyntheticUiCredential694581";
        new LocalEncryptedSecretStore(UserStorage.DirectoryPath).Write(AiSettings.Default.SecretName, secret);
        var button = new Button { Name = "DiagnosticFixtureButton", Content = "测试操作" };
        var selector = new ComboBox { Name = "DiagnosticFixtureChoice", ItemsSource = new[] { "first", "second" } };
        var panel = new StackPanel();
        panel.Children.Add(new TextBox { Text = "PRIVATE_TYPED_TEXT_SENTINEL" });
        panel.Children.Add(new PasswordBox { Password = "PRIVATE_PASSWORD_SENTINEL" });
        panel.Children.Add(button); panel.Children.Add(selector);
        var window = new Window { Owner = owner, Content = panel, Width = 300, Height = 180, ShowInTaskbar = false };
        window.Show();
        await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        selector.SelectedIndex = 1;
        new RelayCommand(() => { }).Execute(null);
        window.Close();
        DiagnosticEventLog.Current!.Record("automatic-sorting", "failed", "中文错误 " + secret);
        Services.UiDiagnosticEvents.Failure("synthetic-failure", new InvalidOperationException("Synthetic crash " + secret));
        var attempt = Path.Combine(UserStorage.DirectoryPath, "Data", "GameTests", "game-smoke");
        Directory.CreateDirectory(attempt);
        foreach (var name in new[] { "session.json", "process.json", "finished.json", "recovered.json" })
            await File.WriteAllTextAsync(Path.Combine(attempt, name), JsonSerializer.Serialize(new { note = "fixture " + secret }));
        await File.WriteAllTextAsync(Path.Combine(attempt, "runtime-Player.log"), string.Concat(Enumerable.Repeat("Synthetic game diagnostic line\n", 100000)));
        owner.ViewModel.Diagnostics.ReplaceWith([new(new("SharedRule", RimWise.Core.DiagnosticSeverity.Warning,
            "Synthetic shared rule", Identifier: "fixture.a") { RelatedIdentifiers = ["fixture.b"] })]);
        var ticks = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1), DispatcherPriority.Background, (_, _) => ticks++, owner.Dispatcher);
        timer.Start();
        var result = await owner.ExportDiagnosticsAsync(Path.Combine(output, "诊断 包.zip"));
        timer.Stop();
        Assert(ticks > 0, "Export blocked dispatcher");
        using var zip = ZipFile.OpenRead(result.Path);
        var files = zip.Entries.ToDictionary(e => e.FullName, e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); });
        Assert(files.Keys.Any(name => name.Contains("crash-")), "Durable crash evidence missing");
        var events = string.Join('\n', files.Where(f => f.Key.EndsWith(".jsonl")).Select(f => f.Value));
        foreach (var operation in new[] { "button-clicked", "selection-changed", "command-executed", "window-opened", "window-closed", "automatic-sorting" })
            Assert(events.Contains(operation), "Missing event " + operation);
        var combined = string.Join('\n', files.Values);
        foreach (var value in new[] { secret, "PRIVATE_TYPED_TEXT_SENTINEL", "PRIVATE_PASSWORD_SENTINEL" })
            Assert(!combined.Contains(value), "Private input captured");
        foreach (var name in new[] { "session.json", "process.json", "finished.json", "recovered.json" })
            Assert(files.Keys.Any(path => path.EndsWith("/" + name)), "Missing lifecycle journal " + name);
        using var application = JsonDocument.Parse(files["application.json"]);
        var state = application.RootElement.GetProperty("currentState");
        Assert(state.GetProperty("warningCount").GetInt32() == 1 && state.GetProperty("errorCount").GetInt32() == 0,
            "Diagnostic counts missing from export");
        var issue = state.GetProperty("diagnostics")[0];
        Assert(issue.GetProperty("code").GetString() == "SharedRule"
            && issue.GetProperty("relatedIdentifiers")[0].GetString() == "fixture.b", "Related diagnostic evidence missing");
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            { passed = true, dispatcherTicks = ticks, files = result.Files, bytes = result.Bytes,
                checks = new[] { "settings entry", "background export", "window and control events", "command events", "private inputs excluded", "keys redacted", "diagnostic details and counts", "lifecycle journals" } }));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
#endif
