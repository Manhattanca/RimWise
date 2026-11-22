using RimWise.Core;
using RimWise.Integration.Configuration;

namespace RimWise.Integration.Scanning;

/// <summary>Trusted, recoverable installation selection for sorting only. No file deletion or unsubscribe.
/// Backups remain usable from the existing recovery UI, including after cancellation or process exit.</summary>
public sealed class AutomaticCopySelection(string dataDirectory, GamePaths paths, GameVersion version)
{
    private readonly DuplicateCopyBackups store = new(dataDirectory, paths);
    internal Func<bool> GameRunning { set => store.GameRunning = value; }
    private readonly Dictionary<string, CopyGroup> groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> unavailable = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ModInstallation[] Copies, DuplicateCopyBackup? Journal)> originals = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> touched = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Messages { get; } = [];
    public IReadOnlySet<string> FallbackPackages => groups.Where(g => g.Value.Selected is not null && !unavailable.Contains(g.Key))
        .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed class CopyGroup(ModInstallation[] copies, DuplicateCopyBackup? journal)
    {
        internal ModInstallation[] Copies = copies;
        internal DuplicateCopyBackup? Journal = journal;
        internal ModInstallation? Selected;
    }

    public SortingInstallationOrder Prepare(ModCatalogue catalogue, IReadOnlyList<string> order, CancellationToken token)
    {
        var active = order.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var journals = store.List().Where(j => j.AutomaticPackageId.Length > 0).ToArray();
        var queue = new Queue<string>(order); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.TryDequeue(out var id))
        {
            token.ThrowIfCancellationRequested();
            var copies = SortingInstallationPolicy.Candidates(catalogue, id, version).ToArray();
            if (copies.Length == 0)
            {
                if (!active.Contains(id) || !SortingInstallationPolicy.RetainUnverified(catalogue, id, version)) unavailable.Add(id);
                continue;
            }
            var key = copies[0].PackageId;
            if (!visited.Add(key)) continue;
            var journal = journals.SingleOrDefault(j => j.AutomaticPackageId.Equals(key, StringComparison.OrdinalIgnoreCase));
            ModInstallation? selected;
            if (copies.Length > 1 || journal is not null)
            {
                if (copies.Any(c => c.Source == ModSource.Game) || GameContentPolicy.KeepWhenClearing(key, version))
                    throw new InvalidDataException("游戏内容的重复安装需要修复游戏文件，未移动任何游戏内容：" + copies[0].Name);
                if (journal is not null)
                {
                    // Never overwrite a directory recreated by Steam; Restore performs the same check again.
                    if (copies.Length != 1 || !copies[0].Directory.Equals(journal.Selected, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("副本备份的原位置发生变化，请先在“副本备份”核对恢复记录：" + copies[0].Name);
                    copies = new[] { copies[0] }.Concat(journal.Moves.Select(move => ReadBackup(copies[0], move, journal))).ToArray();
                }
                var group = new CopyGroup(copies, journal);
                originals.TryAdd(key, (copies, journal));
                groups[key] = group;
                selected = Select(group, active, token);
                if (selected is null) unavailable.Add(key);
            }
            else selected = copies[0];
            if (selected is not null)
                foreach (var dependency in selected.Metadata?.Requirements.Dependencies ?? []) queue.Enqueue(dependency.PackageId);
        }
        return Normalize(catalogue, order);
    }

    public void AttachForRecovery(ModCatalogue catalogue, IReadOnlyList<string> order, CancellationToken token)
    {
        // Read-only attachment: never change the installation context of a paid checkpoint.
        var journals = store.List().Where(j => j.AutomaticPackageId.Length > 0);
        foreach (var journal in journals)
        {
            token.ThrowIfCancellationRequested();
            var selected = catalogue.Installations.SingleOrDefault(m => m.Directory.Equals(journal.Selected, StringComparison.OrdinalIgnoreCase));
            if (selected is null || !order.Any(id => catalogue.Resolve(id, version).UniqueInstallation?.Directory == selected.Directory)) continue;
            var copies = new[] { selected }.Concat(journal.Moves.Select(m => ReadBackup(selected, m, journal))).ToArray();
            groups[selected.PackageId] = new(copies, journal) { Selected = selected };
            originals.TryAdd(selected.PackageId, (copies, journal));
        }
    }

    public void Rollback(CancellationToken token)
    {
        foreach (var (key, group) in groups.Reverse())
        {
            if (!touched.Contains(key) || group.Journal is null || !originals.TryGetValue(key, out var original)) continue;
            var current = store.List().SingleOrDefault(j => j.Id == group.Journal.Id);
            if (current is not null) store.Restore(current.Id, token);
            group.Journal = null;
            if (original.Journal is not null)
            {
                var selected = original.Copies.Single(m => m.Directory.Equals(original.Journal.Selected, StringComparison.OrdinalIgnoreCase));
                var plan = store.PlanAutomatic(original.Copies, selected) with { RejectedDirectories = current?.RejectedDirectories ?? [] };
                group.Journal = plan;
                store.Apply(plan, token);
                group.Selected = selected;
            }
            touched.Remove(key);
        }
        Messages.Add("排序未保存，已恢复本轮自动选择前的副本目录状态；没有删除模组文件。");
    }

    public SortingInstallationOrder Retry(ModCatalogue catalogue, IReadOnlyList<string> order, string packageId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!groups.TryGetValue(packageId, out var group) || group.Selected is null || group.Journal is null)
            throw new InvalidDataException("找不到本轮使用的自动副本记录。");
        store.RejectSelected(group.Journal.Id);
        group.Journal = store.List().Single(j => j.Id == group.Journal.Id);
        Messages.Add("本轮报错关联到副本，改用其他副本作对照测试（尚未证明根因）：" + group.Selected.Name
            + " · " + group.Selected.FolderName);
        if (Select(group, order.ToHashSet(StringComparer.OrdinalIgnoreCase), token) is null) unavailable.Add(packageId);
        // Dependencies of an alternative release can differ. Re-run preparation on the refreshed
        // catalogue in the caller before capturing the next AI task's recovery context.
        return Normalize(catalogue, order);
    }

    private ModInstallation ReadBackup(ModInstallation selected, DuplicateCopyMove move, DuplicateCopyBackup journal)
    {
        if (!journal.Moves.Contains(move) || !journal.Selected.Equals(selected.Directory, StringComparison.OrdinalIgnoreCase)
            || !journal.AutomaticPackageId.Equals(selected.PackageId, StringComparison.OrdinalIgnoreCase)
            || !ModMetadataReader.IsValidRecoveredPackageId(journal.AutomaticPackageId))
            throw new InvalidDataException("备份副本的身份记录不匹配。");
        ReadOnlyMetadata.CheckPath(move.Backup, move.Backup);
        var file = Path.Combine(move.Backup, "About", "About.xml");
        ModMetadata? metadata = null;
        RecoveredIdentity? identity = null;
        var diagnostics = new List<CatalogueDiagnostic>();
        try { metadata = new ModMetadataReader().Read(file, move.Backup, version); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            diagnostics.Add(new("MetadataUnreadable", DiagnosticSeverity.Warning,
                "备份副本信息未完整读取，保留待测试：" + ex.Message, move.Original));
            try { identity = new ModMetadataReader().ReadIdentity(file, move.Backup); }
            catch (Exception identityError) when (identityError is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException) { }
            identity ??= new("", journal.AutomaticPackageId, "Validated automatic backup journal");
        }
        if (!string.Equals(metadata?.PackageId ?? identity?.PackageId, selected.PackageId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份副本的模组标识发生变化。");
        var workshop = paths.WorkshopDirectory.Length > 0
            && Path.GetDirectoryName(move.Original)!.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.WorkshopDirectory)), StringComparison.OrdinalIgnoreCase);
        return new(move.Original, Path.GetFileName(move.Original), workshop ? ModSource.Workshop : ModSource.Local,
            workshop ? Path.GetFileName(move.Original) : "", metadata, diagnostics.AsReadOnly()) { RecoveredIdentity = identity };
    }

    private DuplicateCopyInspection Inspect(CopyGroup group, ModInstallation mod, IReadOnlySet<string> active, CancellationToken token)
    {
        var move = group.Journal?.Moves.SingleOrDefault(m => m.Original.Equals(mod.Directory, StringComparison.OrdinalIgnoreCase));
        var physical = move?.Backup ?? mod.Directory;
        if (move is not null && Directory.Exists(move.Original))
            throw new IOException("Steam 或其他程序已重建副本原目录，未覆盖任何文件：" + mod.Name);
        return DuplicateCopyInspector.Inspect(mod with { Directory = physical }, version, active, token) with { Installation = mod };
    }

    private ModInstallation? Select(CopyGroup group, IReadOnlySet<string> active, CancellationToken token)
    {
        var rejected = group.Journal?.RejectedDirectories ?? [];
        var inspections = group.Copies.Select(mod => Inspect(group, mod, active, token)).ToArray();
        var selected = DuplicateCopyInspector.Rank(inspections).FirstOrDefault(c => !c.HasFileErrors
            && !rejected.Contains(c.Installation.Directory, StringComparer.OrdinalIgnoreCase));
        foreach (var copy in inspections.Where(c => c.HasFileErrors))
            Messages.Add("跳过文件结构不可用的副本：" + copy.Installation.Name + " · " + copy.Installation.FolderName + "\n" + copy.Status);
        foreach (var copy in inspections.Where(c => !c.HasFileErrors && c.Limited))
            Messages.Add("副本检查不完整，保留待测试：" + copy.Installation.Name + " · " + copy.Installation.FolderName + "\n" + copy.Status);
        if (selected is null)
        {
            group.Selected = null;
            Messages.Add("可用副本已用尽，仅剔除排序项，不删除安装文件：" + group.Copies[0].Name);
            return null;
        }
        if (group.Journal?.Selected.Equals(selected.Installation.Directory, StringComparison.OrdinalIgnoreCase) != true)
        {
            // Validate all inspected evidence again before changing which directories the game sees.
            foreach (var old in inspections)
            {
                var fresh = Inspect(group, old.Installation, active, token);
                if (fresh.Fingerprint != old.Fingerprint || fresh.Status != old.Status)
                    throw new IOException("检查期间副本文件发生变化，请刷新后重试。");
            }
            touched.Add(selected.Installation.PackageId);
            if (group.Journal is not null) store.Restore(group.Journal.Id, token);
            var plan = store.PlanAutomatic(group.Copies, selected.Installation) with { RejectedDirectories = rejected };
            group.Journal = plan;
            store.Apply(plan, token);
        }
        group.Selected = selected.Installation;
        Messages.Add("自动使用副本：" + selected.Installation.Name + " · "
            + DuplicateInstallations.SourceName(selected.Installation.Source) + " · 模组版本 "
            + (selected.Version.Length == 0 ? "未声明" : selected.Version) + " · " + selected.Installation.FolderName
            + "（其他副本已保留可恢复备份；运行情况以复测为准）");
        return group.Selected;
    }

    private SortingInstallationOrder Normalize(ModCatalogue catalogue, IReadOnlyList<string> order)
    {
        var selected = new ModCatalogue(catalogue.Installations.Where(mod => !groups.TryGetValue(mod.PackageId, out var group)
            || group.Selected is null || mod.Directory.Equals(group.Selected.Directory, StringComparison.OrdinalIgnoreCase))
            // A newly selected directory was previously outside the scan root.
            .Concat(groups.Values.Select(g => g.Selected).OfType<ModInstallation>())
            .DistinctBy(mod => mod.Directory, StringComparer.OrdinalIgnoreCase).ToArray(), catalogue.UnverifiedOrderIdentifiers);
        var result = SortingInstallationPolicy.Normalize(catalogue, selected, order, version, unavailable);
        Messages.AddRange(result.Messages);
        return result;
    }
}
