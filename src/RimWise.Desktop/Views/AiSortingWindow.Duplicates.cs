using System.IO;
using RimWise.Core;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Scanning;
using RimWise.Integration.Game;
using RimWise.Desktop.Presentation;
using System.Windows;

namespace RimWise.Desktop.Views;

public partial class AiSortingWindow
{
    private IReadOnlyList<OrderMatch> RequiredDuplicateInstallations() => editor.CurrentCatalogue is { } catalogue
        ? DuplicateInstallations.FindRequired(catalogue, editor.Rows.Select(row => row.PackageId).ToArray(),
            editor.EffectiveGameVersion) : [];

    private bool ResolveDuplicateInstallations(CancellationToken cancellation)
    {
        var duplicates = RequiredDuplicateInstallations();
        if (duplicates.Count == 0) return true;
        DiagnosticEventLog.Current?.Record("automatic-sorting", "duplicate-installations",
            string.Join("\n\n", duplicates.Select(DuplicateInstallations.Describe)));
        var window = new DuplicateModsWindow(duplicates, RefreshDuplicateInstallationsAsync, ChooseDuplicateCopyAsync,
            editor.EffectiveGameVersion,
            editor.Rows.Select(r => r.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase)) { Owner = this };
        using var registration = cancellation.Register(() => Dispatcher.BeginInvoke(new Action(() =>
        { if (window.IsVisible) window.Close(); })));
        cancellation.ThrowIfCancellationRequested();
        return window.ShowDialog() == true;
    }

    internal async Task<bool> ChooseDuplicateCopyAsync(IReadOnlyList<DuplicateCopyInspection> group,
        DuplicateCopyInspection selected, CancellationToken cancellation)
    {
        if (gamePaths is null) return false;
        var paths = gamePaths();
        var revision = editor.ContentRevision;
        var store = new DuplicateCopyBackups(dataDirectory, paths);
        var plan = store.Plan(group.Select(c => c.Installation).ToArray(), selected.Installation);
        var detail = "使用：" + selected.Installation.Name + "\n" + selected.Installation.Directory
            + "\n版本：" + (selected.Version.Length > 0 ? selected.Version : "未知") + "\n" + selected.Status
            + "\n\n以下副本将移入备份（不删除、不取消订阅）：\n"
            + string.Join("\n\n", plan.Moves.Select(m => m.Original + "\n→ " + m.Backup))
            + "\n\n此选择也会影响以后正常启动游戏。可在自动排序页面的“副本备份”恢复。是否继续？";
        if (MessageDialog.Show(this, detail, "确认使用此副本", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;
        cancellation.ThrowIfCancellationRequested();
        if (revision != editor.ContentRevision || paths != gamePaths()) throw new InvalidOperationException("排序或路径已变化，请重新选择。");
        var version = editor.EffectiveGameVersion;
        var active = editor.Rows.Select(r => r.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await Task.Run(() =>
        {
            foreach (var candidate in group)
            {
                var fresh = DuplicateCopyInspector.Inspect(candidate.Installation, version, active, cancellation);
                if (fresh.Fingerprint != candidate.Fingerprint || fresh.Status != candidate.Status)
                    throw new IOException("副本文件在确认期间发生变化，请刷新后重新选择。");
            }
            store.Apply(plan, cancellation);
        }, cancellation);
        DiagnosticEventLog.Current?.Record("duplicate-copies", "user-selected", $"journal={plan.Id}; selected={plan.Selected}");
        return true;
    }

    private void CopyBackups_Click(object sender, RoutedEventArgs e)
    {
        if (IsWorking || gamePaths is null) return;
        try
        {
            var store = new DuplicateCopyBackups(dataDirectory, gamePaths());
            var window = new DuplicateCopyBackupsWindow(store, RefreshDuplicateInstallationsAsync) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex) when (IsExpectedError(ex))
        { MessageDialog.Show(this, ex.Message, "副本备份", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    internal async Task<IReadOnlyList<OrderMatch>> RefreshDuplicateInstallationsAsync(CancellationToken cancellation)
    {
        if (gamePaths is null) throw new InvalidOperationException("游戏路径尚未配置。");
        var paths = gamePaths();
        var revision = editor.ContentRevision;
        var previous = editor.CurrentCatalogue;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, ownerLifetime);
        var scan = await Task.Run(() => new ModScanner().Scan(paths, linked.Token, inspectContent: false), linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (closed || revision != editor.ContentRevision || paths != gamePaths() || !ReferenceEquals(previous, editor.CurrentCatalogue))
            throw new InvalidOperationException("排序或路径已改变，请返回后重新开始。");
        if (scan.Diagnostics.Any(issue => issue.Code is "ScanRootUnreadable" or "ScanLimit" or "NoScanPaths"))
            throw new IOException("部分安装目录未读取完整，保留上次结果。请检查路径与访问权限。");
        // This refresh needs only metadata. Preserve already inspected badges and times.
        var known = previous?.Installations.ToDictionary(mod => mod.Directory, StringComparer.OrdinalIgnoreCase);
        scan = scan with { Catalogue = new(scan.Catalogue.Installations.Select(mod =>
            known?.GetValueOrDefault(mod.Directory) is { } old ? mod with
            { CodeKind = old.CodeKind, ContentKinds = old.ContentKinds, LastUpdatedUtc = old.LastUpdatedUtc } : mod).ToArray()) };
        editor.ApplyScan(scan);
        return RequiredDuplicateInstallations();
    }
}
