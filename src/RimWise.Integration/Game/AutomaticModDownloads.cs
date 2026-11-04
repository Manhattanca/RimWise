using System.ComponentModel;
using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Scanning;
using RimWise.Integration.Steam;

namespace RimWise.Integration.Game;

internal sealed record AutomaticDownloadResult(ModCatalogue Catalogue, IReadOnlyDictionary<string,string> Failures);

/// <summary>Queue the entire batch before waiting. Only confirmed, identity-checked installations enter the catalogue.</summary>
internal static class AutomaticModDownloads
{
    internal static async Task<AutomaticDownloadResult> RunAsync(IReadOnlyList<ImportedModDownload> pending,
        ModCatalogue catalogue, GameVersion version, string workshop,
        Func<string,CancellationToken,Task<IReadOnlyList<WorkshopItemState>>> enqueue,
        Func<string,CancellationToken,Task<IReadOnlyList<WorkshopItemState>>> wait, CancellationToken token)
    {
        var failures = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var queued = new HashSet<string>(StringComparer.Ordinal);
        var installed = catalogue.Installations.ToList();
        foreach (var group in pending.GroupBy(item=>item.WorkshopId,StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            try { await enqueue(group.Key,token); queued.Add(group.Key); }
            catch (Exception ex) when (Recoverable(ex))
            { token.ThrowIfCancellationRequested(); foreach(var item in group) failures[item.Identifier]=ex.Message; }
        }
        foreach (var group in pending.GroupBy(item=>item.WorkshopId,StringComparer.Ordinal).Where(g=>queued.Contains(g.Key)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var states = await wait(group.Key,token);
                token.ThrowIfCancellationRequested();
                var changed = new ModScanner().ReadWorkshopItems(workshop,[group.Key],version,token);
                var paths = changed.Select(mod=>mod.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var candidate = new ModCatalogue(installed.Where(mod=>!paths.Contains(mod.Directory)).Concat(changed).ToArray());
                var plan = new ImportDownloadPlan(group.ToArray(),[]);
                ImportedOrderDownloads.VerifyInstallations(candidate,plan,version,states);
                WorkshopDependencyDownloads.VerifyInstalled(candidate,workshop,version,group.Select(i=>new RequiredModDownload(i.Identifier,i.WorkshopId) { DiscoverPackageId = i.DiscoverPackageId }));
                installed=candidate.Installations.ToList();
            }
            catch (Exception ex) when (Recoverable(ex))
            { token.ThrowIfCancellationRequested(); foreach(var item in group) failures[item.Identifier]=ex.Message; }
        }
        return new(new(installed),failures);
    }

    private static bool Recoverable(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or Win32Exception or TimeoutException or JsonException;
}
