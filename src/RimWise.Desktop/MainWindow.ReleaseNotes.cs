using System.IO;
using System.Text.Json;
using System.Windows;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Updates;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private bool loadingReleaseNotes;
    internal async Task ShowInstalledReleaseNotesAsync(string? installedVersion = null,
        Func<string, CancellationToken, Task<InstalledReleaseNotes?>>? fetch = null,
        Action<ReleaseNotesWindow>? show = null)
    {
        var version = installedVersion ?? ApplicationBuildIdentity.ProductVersion(typeof(MainWindow).Assembly)?.Split('+')[0];
        // Development timestamps are not published release tags.
        if (version is null || !InstalledReleaseNotes.IsStableVersion(version) || loadingReleaseNotes || lifetime.IsCancellationRequested) return;
        loadingReleaseNotes = true;
        try
        {
            var store = new ReleaseNotesStore(UserStorage.DirectoryPath);
            if (store.HasShown(version)) return;
            using var lease = store.Acquire(version);
            if (store.HasShown(version)) return;
            InstalledReleaseNotes? notes = null;
            try { notes = store.Read(version); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { /* Fetch a fresh exact-tag copy. */ }
            if (notes is null)
            {
                using var http = GitHubReleaseUpdater.CreateHttpClient();
                using var mirror = GitHubReleaseUpdater.CreateMirrorHttpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                notes = await (fetch?.Invoke(version, timeout.Token)
                    ?? new GitHubReleaseUpdater(http, mirror).ReadReleaseNotesAsync(version, timeout.Token));
                if (notes is null || notes.Version != version) return;
                store.Cache(notes);
            }
            if (lifetime.IsCancellationRequested || !IsVisible) return;
            // Do not interrupt another modal operation that the user opened while fetching.
            while (OwnedWindows.OfType<Window>().Any(window => window.IsVisible) || settingsOpen || OpacityPicker is not null)
                await Task.Delay(250, lifetime.Token);
            if (lifetime.IsCancellationRequested || !IsVisible) return;
            var dialog = new ReleaseNotesWindow(this, notes);
            dialog.ContentRendered += async (_, _) =>
            {
                // ContentRendered can still be cloaked. Count only a successfully
                // presented dialog, not a failed/closed-before-reveal window.
                if (SecondaryWindowPresentation.GetPresentation(dialog) is { } presentation && !await presentation.Ready) return;
                if (!dialog.IsVisible || lifetime.IsCancellationRequested) return;
                try { store.MarkShown(version); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { DiagnosticEventLog.Current?.Record("update", "notes-receipt-failed", ex.GetType().Name); }
            };
            if (show is null) dialog.ShowDialog(); else show(dialog);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException
            or JsonException or InvalidOperationException or ArgumentException)
        {
            DiagnosticEventLog.Current?.Record("update", "notes-unavailable", ex.GetType().Name);
        }
        finally { loadingReleaseNotes = false; }
    }

    private static void CacheReleaseNotes(ReleaseDownload release)
    {
        if (string.IsNullOrWhiteSpace(release.ReleaseNotes)) return;
        try { new ReleaseNotesStore(UserStorage.DirectoryPath).Cache(new(release.Version, release.ReleaseNotes)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { DiagnosticEventLog.Current?.Record("update", "notes-cache-failed", ex.GetType().Name); }
    }
}
