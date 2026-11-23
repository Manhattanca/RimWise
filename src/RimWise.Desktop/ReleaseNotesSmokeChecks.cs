#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Integration.Updates;

namespace RimWise.Desktop;

internal static class ReleaseNotesSmokeChecks
{
    internal static async Task Run(MainWindow main, string output, Action<bool, string> check)
    {
        var store = new ReleaseNotesStore(UserStorage.DirectoryPath);
        const string version = "812.1.0";
        const string body = "优化透明度设置。\n\n修复启动画面。\n\n<script>这只是文字，不会执行。</script>";
        var calls = 0; var shown = 0;
        Task<InstalledReleaseNotes?> Fetch(string requested, CancellationToken token)
        { calls++; check(requested == version, "release notes lookup uses installed version rather than latest"); return Task.FromResult<InstalledReleaseNotes?>(new(version, body)); }
        await main.ShowInstalledReleaseNotesAsync(version, Fetch, dialog =>
        {
            shown++;
            check(!store.HasShown(version), "unrendered release dialog does not consume the first-start receipt");
            dialog.ContentRendered += async (_, _) =>
            {
                if (SecondaryWindowPresentation.GetPresentation(dialog) is { } presentation) await presentation.Ready;
                await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                check(dialog.NotesText.Text == body && dialog.Owner == main, "release dialog shows exact GitHub text as inert content");
                check(dialog.DismissButton.IsDefault && dialog.DismissButton.IsCancel, "release dialog supports Enter and Escape");
                check(dialog.DismissButton.Background is System.Windows.Media.SolidColorBrush { Color.A: 255 }, "release close action remains readable with transparent themes");
                check(store.HasShown(version), "release receipt is written after the first rendered frame");
                PrototypeSmokeCheck.SavePreview(dialog, Path.Combine(output, "release-notes.png"));
                dialog.DismissButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            dialog.ShowDialog();
        });
        await main.ShowInstalledReleaseNotesAsync(version, Fetch, _ => shown++);
        check(calls == 1 && shown == 1, "second launch neither fetches nor opens the acknowledged release");
        var cacheVersion = "812.1.1"; store.Cache(new(cacheVersion, body));
        await main.ShowInstalledReleaseNotesAsync(cacheVersion, (_, _) => throw new Exception("Cached text must work offline"), dialog =>
        { check(dialog.NotesText.Text == body, "next release has independent cached notes available offline"); dialog.Close(); });
        check(!store.HasShown(cacheVersion), "closing before render does not mark the next version seen");
        await main.ShowInstalledReleaseNotesAsync("812.1.1-dev.123", (_, _) => throw new Exception("Development build must not fetch a release"));
        var attempts = 0;
        for (var i = 0; i < 2; i++)
            await main.ShowInstalledReleaseNotesAsync("812.1.2", (_, _) => { attempts++; throw new System.Net.Http.HttpRequestException("offline"); });
        check(attempts == 2 && !store.HasShown("812.1.2"), "offline failures keep main usable and retry on a later launch");
    }
}
#endif
