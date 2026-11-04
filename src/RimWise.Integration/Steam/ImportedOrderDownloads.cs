using RimWise.Core;
using RimWise.Formats;
using RimWise.Integration.Databases;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Steam;

public sealed record ImportedModDownload(string Identifier, string WorkshopId)
{
    public string Notice { get; init; } = "";
    public bool DiscoverPackageId { get; init; }
    public string Reason { get; init; } = "";
}
public sealed record ImportedModIssue(string Identifier, string Reason, bool CanSubscribe = true);
public sealed record ImportDownloadPlan(IReadOnlyList<ImportedModDownload> Downloads, IReadOnlyList<string> Unresolved)
{
    public IReadOnlyList<ImportedModIssue> Issues { get; init; } = [];
}

/// <summary>Resolves only missing imported entries. An import never changes the player's chosen order.</summary>
public static class ImportedOrderDownloads
{
    public static ImportDownloadPlan Plan(OrderFileDocument document, ModCatalogue catalogue, GameVersion? version,
        DatabaseSnapshot? database) => Plan(document.Configuration.Order.PackageIds, document.WorkshopReferences, catalogue, version, database);

    public static ImportDownloadPlan Plan(IEnumerable<string> order, IReadOnlyList<OrderWorkshopReference> workshopReferences,
        ModCatalogue catalogue, GameVersion? version, DatabaseSnapshot? database, bool automatic = false)
    {
        var downloads = new List<ImportedModDownload>();
        var unresolved = new List<string>();
        var issues = new List<ImportedModIssue>();
        var dependencies = catalogue.Installations.Where(m => m.Metadata is not null)
            .SelectMany(m => m.Metadata!.Requirements.Dependencies).Where(d => ValidId(d.WorkshopId))
            .GroupBy(d => d.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(d => d.WorkshopId).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);
        var references = workshopReferences.GroupBy(r => r.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.WorkshopId.Trim()).Where(id => id.Length > 0 && id != "0")
                .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var id in order.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (catalogue.Resolve(id, version).Candidates.Count > 0) continue;
            if (id.StartsWith("ludeon.rimworld", StringComparison.OrdinalIgnoreCase) || id.Equals("Core", StringComparison.OrdinalIgnoreCase))
            { Unresolved(id, "本体或 DLC 不能从创意工坊订阅", false); continue; }
            try
            {
                var hints = references.GetValueOrDefault(id, []);
                if (hints.Length > 1) throw new InvalidDataException("文件中的工坊编号有冲突");
                string workshop;
                var notice = "";
                var package = id.EndsWith("_steam", StringComparison.OrdinalIgnoreCase) ? id[..^6] : id;
                if (hints.Length == 1) workshop = hints[0];
                else if (version?.UsesLegacyIdentifiers == true && ValidId(id)) workshop = id;
                else if (dependencies.TryGetValue(package, out var declared))
                {
                    if (declared.Length != 1) throw new InvalidDataException("已安装模组声明了不同工坊来源，请填写要订阅的编号");
                    workshop = declared[0];
                }
                else if (database is not null && version?.UsesLegacyIdentifiers != true)
                {
                    var found = automatic && version is not null
                        ? (Id: database.RequiredWorkshopId(package, version), Compatible: true)
                        : database.ImportedWorkshopId(package, version);
                    workshop = found.Id;
                    if (!found.Compatible) notice = "数据库未声明支持当前游戏版本；订阅后仍需核对兼容性";
                }
                else throw new InvalidDataException("未找到可确认的工坊来源，可填写 Steam 工坊链接或编号");
                if (!ValidId(workshop)) throw new InvalidDataException("工坊编号无效");
                if (automatic) database?.ValidateRequiredWorkshopId(package, workshop);
                var discover = catalogue.Installations.Any(m => m.Metadata?.Requirements.Dependencies.Any(d =>
                    d.WorkshopIdentityOnly && d.PackageId.Equals(id,StringComparison.OrdinalIgnoreCase) && d.WorkshopId == workshop) == true);
                downloads.Add(new(id, workshop) { Notice = notice, DiscoverPackageId = discover });
            }
            catch (InvalidDataException ex) { Unresolved(id, ex.Message); }
        }
        return new(downloads.AsReadOnly(), unresolved.AsReadOnly()) { Issues = issues.AsReadOnly() };

        void Unresolved(string id, string reason, bool canSubscribe = true)
        { unresolved.Add(id + "：" + reason); issues.Add(new(id, reason, canSubscribe)); }
    }

    public static Task<IReadOnlyList<WorkshopItemState>> DownloadAsync(ImportDownloadPlan plan, string applicationDirectory,
        IProgress<string>? progress, CancellationToken token) => DownloadAsync(plan,
            WorkshopDependencyDownloads.VerifyRimWorldItemAsync,
            (id, cancellation) => new SteamWorkshopClient(applicationDirectory).SubscribeAndDownloadAsync(id, progress, cancellation),
            progress, token);

    public static async Task EnqueueAsync(ImportDownloadPlan plan, string applicationDirectory, IProgress<string>? progress, CancellationToken token)
    {
        var client = new SteamWorkshopClient(applicationDirectory);
        foreach (var id in plan.Downloads.Select(d => d.WorkshopId).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (!ValidId(id)) throw new InvalidDataException("工坊编号无效。");
            // The bridge verifies AppID through SteamUGC before subscribing; no alternate HTTP download path is used.
            await client.EnqueueSubscriptionAsync(id, progress, token);
        }
    }

    public static Task<IReadOnlyList<WorkshopItemState>> WaitAsync(ImportDownloadPlan plan, string applicationDirectory,
        IProgress<string>? progress, CancellationToken token) => DownloadAsync(plan,
            (_, cancellation) => { cancellation.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            (id, cancellation) => new SteamWorkshopClient(applicationDirectory).WaitForDownloadAsync(id, progress, cancellation), progress, token);

    // Separate transport from planning so completion, failure and cancellation can be checked without changing subscriptions.
    public static async Task<IReadOnlyList<WorkshopItemState>> DownloadAsync(ImportDownloadPlan plan,
        Func<string, CancellationToken, Task> verify,
        Func<string, CancellationToken, Task<IReadOnlyList<WorkshopItemState>>> subscribe,
        IProgress<string>? progress, CancellationToken token)
    {
        var completed = new List<WorkshopItemState>();
        foreach (var id in plan.Downloads.Select(d => d.WorkshopId).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (!ValidId(id)) throw new InvalidDataException("工坊编号无效。");
            progress?.Report("正在核对创意工坊项目：" + id);
            await verify(id, token);
            token.ThrowIfCancellationRequested();
            progress?.Report("正在订阅并等待 Steam 下载：" + id);
            var result = await subscribe(id, token);
            token.ThrowIfCancellationRequested();
            if (result.Count != 1 || result[0].Id != id || !result[0].Subscribed || !result[0].Installed
                || string.IsNullOrWhiteSpace(result[0].Directory))
                throw new IOException("Steam 尚未确认下载完成，保留当前排序；已发出的订阅可能仍在下载。");
            completed.Add(result[0]);
        }
        return completed.AsReadOnly();
    }

    public static void VerifyInstallations(ModCatalogue catalogue, ImportDownloadPlan plan, GameVersion? version,
        IReadOnlyList<WorkshopItemState> completed)
    {
        foreach (var item in plan.Downloads)
        {
            var state = completed.SingleOrDefault(s => s.Id == item.WorkshopId);
            var matches = item.DiscoverPackageId ? catalogue.Installations.Where(m => m.Source == ModSource.Workshop && m.WorkshopId == item.WorkshopId && m.Metadata is not null).ToArray() : [];
            var mod = item.DiscoverPackageId ? matches.Length == 1 ? catalogue.Resolve(matches[0].PackageId,version).UniqueInstallation : null
                : catalogue.Resolve(item.Identifier, version).UniqueInstallation;
            if (state is not { Installed: true, Subscribed: true } || mod?.Source != ModSource.Workshop
                || mod.WorkshopId != item.WorkshopId || !string.Equals(ScopedPaths.Ordinary(mod.Directory, true),
                    ScopedPaths.Ordinary(state.Directory, true), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载后的模组标识或安装目录与排序文件不符：" + item.Identifier + "；已保留当前排序，请核对来源。");
        }
    }

    private static bool ValidId(string id) => id.Length > 0 && id.All(char.IsAsciiDigit)
        && ulong.TryParse(id, out var number) && number > 0 && number.ToString(System.Globalization.CultureInfo.InvariantCulture) == id;

    public static string? WorkshopIdFromInput(string? input)
    {
        var text = input?.Trim() ?? "";
        if (ValidId(text)) return text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.Equals("www.steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.TrimEnd('/') is not ("/sharedfiles/filedetails" or "/workshop/filedetails")) return null;
        var ids = uri.Query.TrimStart('?').Split('&').Where(p => p.StartsWith("id=", StringComparison.Ordinal)).Select(p => p[3..]).ToArray();
        return ids.Length == 1 && ValidId(ids[0]) ? ids[0] : null;
    }
}
