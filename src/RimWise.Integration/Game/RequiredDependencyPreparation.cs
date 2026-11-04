using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Databases;
using RimWise.Integration.Steam;

namespace RimWise.Integration.Game;

/// <summary>Reach a dependency fixed point before starting a game or asking an AI.
/// Downloading a prerequisite can expose another prerequisite in its own versioned metadata.</summary>
internal static class RequiredDependencyPreparation
{
    internal static async Task<AutomaticDownloadResult> RunAsync(ModCatalogue catalogue,
        IReadOnlyList<string> order, IReadOnlyList<string> retained, GameVersion version,
        DatabaseSnapshot? database, IReadOnlyList<ConflictRemovalApproval> approvals,
        Func<ModCatalogue, IReadOnlyList<ImportedModDownload>, CancellationToken, Task<AutomaticDownloadResult>> download,
        CancellationToken token, Action<IReadOnlyList<string>>? reportReasons = null)
    {
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            catalogue = database?.WithRequiredDependencies(catalogue, order, version) ?? catalogue;
            var expanded = AiOrderPlanner.ExpandRequiredOrder(order, catalogue, version, allowMissing: true);
            expanded = DeclaredConflictResolution.Replay(catalogue, expanded, retained, version, approvals);
            // Inactive mods must not supply or contradict an active dependency's download origin.
            var active = new ModCatalogue(expanded.Select(id => catalogue.Resolve(id, version).UniqueInstallation)
                .OfType<ModInstallation>().ToArray());
            var reasons = RequiredDependencyReasons.Build(catalogue, order, expanded, version);
            reportReasons?.Invoke(reasons.Values.ToArray());
            var plan = ImportedOrderDownloads.Plan(expanded, [], active, version, database, automatic: true);
            foreach (var issue in plan.Issues) failures[issue.Identifier] = issue.Reason;
            var pending = plan.Downloads.Where(item => !attempted.Contains(item.WorkshopId))
                .Select(item => item with { Reason = reasons.GetValueOrDefault(item.Identifier, "原排序缺失模组：" + item.Identifier) }).ToArray();
            if (pending.Length == 0) return new(catalogue, failures);
            foreach (var item in pending) attempted.Add(item.WorkshopId);
            // Bound total work, rather than rejecting an otherwise valid chain at 20 waves.
            if (attempted.Count > 20_000) throw new InvalidDataException("必要前置下载超过支持的记录数量。");
            var batch = await download(catalogue, pending, token);
            token.ThrowIfCancellationRequested();
            foreach (var failure in batch.Failures) failures[failure.Key] = failure.Value;
            catalogue = batch.Catalogue;
        }
    }
}
