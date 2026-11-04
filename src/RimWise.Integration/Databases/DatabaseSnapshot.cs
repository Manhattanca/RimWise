using System.Collections.Frozen;
using System.Text.Json.Serialization;
using RimWise.Core;

namespace RimWise.Integration.Databases;

internal sealed record CommunityRule(string PackageId, string[] Before, string[] After, string[] Incompatible,
    bool Top, bool Bottom);
internal sealed record WorkshopEntry(string Id, string PackageId, string[] Versions, bool App, bool Unpublished)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? Dependencies { get; init; }
}
internal sealed record DatabaseDocument(int Schema, string CommunityCommit, string CommunityHash,
    string WorkshopCommit, string WorkshopHash, CommunityRule[] Community, WorkshopEntry[] Workshop)
{
    // Omit absent fields to preserve hashes of snapshots written by earlier releases.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CommunityOrigin { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? WorkshopOrigin { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RequiredItemsParsed { get; init; }
}

/// <summary>Immutable local facts. Descriptions, comments, URLs and code never become executable instructions.</summary>
public sealed class DatabaseSnapshot
{
    internal DatabaseDocument Document { get; }
    private readonly FrozenDictionary<string, CommunityRule> community;
    private readonly FrozenDictionary<string, WorkshopEntry[]> workshop;
    private readonly FrozenDictionary<string, WorkshopEntry> workshopIds;
    public string Id { get; }
    public string CommunityCommit => Document.CommunityCommit;
    public string WorkshopCommit => Document.WorkshopCommit;
    public string CommunityOrigin => Document.CommunityOrigin ?? "RimSort/Community-Rules-Database";
    public string WorkshopOrigin => Document.WorkshopOrigin ?? "RimSort/Steam-Workshop-Database";
    public int RulePackages => community.Count;
    public int IndexedItems => Document.Workshop.Length;

    internal DatabaseSnapshot(DatabaseDocument document, string id)
    {
        Document = document; Id = id;
        community = document.Community.ToFrozenDictionary(r => r.PackageId, StringComparer.OrdinalIgnoreCase);
        workshop = document.Workshop.GroupBy(r => r.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        workshopIds = document.Workshop.ToFrozenDictionary(e => e.Id, StringComparer.Ordinal);
    }

    /// <summary>Enrich download hints for locally declared prerequisites only. Workshop required-item
    /// lists may describe optional supported families (for example translation packs); they must
    /// never introduce hard dependencies or enable unrelated installed mods.</summary>
    public ModCatalogue WithRequiredDependencies(ModCatalogue catalogue, IReadOnlyList<string> order, GameVersion version)
    {
        var replacements = new Dictionary<string, ModInstallation>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(order);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var id))
        {
            if (!visited.Add(id)) continue;
            if (visited.Count > 20_000) throw new InvalidDataException("必要前置链超过支持范围。");
            var mod = catalogue.Resolve(id, version).UniqueInstallation;
            if (mod?.Metadata is not { } metadata) continue;
            var dependencies = metadata.Requirements.Dependencies.Select(dependency =>
            {
                if (dependency.WorkshopId.Length > 0) return dependency;
                try { return dependency with { WorkshopId = RequiredWorkshopId(dependency.PackageId, version) }; }
                catch (InvalidDataException) { return dependency; } // The planner reports unresolved/ambiguous origins.
            }).ToArray();
            if (!dependencies.SequenceEqual(metadata.Requirements.Dependencies))
                replacements[mod.Directory] = mod with { Metadata = metadata with
                    { Requirements = metadata.Requirements with { Dependencies = dependencies } } };
            foreach (var dependency in dependencies) pending.Enqueue(dependency.PackageId);
        }
        return replacements.Count == 0 ? catalogue : new(catalogue.Installations
            .Select(m => replacements.GetValueOrDefault(m.Directory, m)).ToArray());
    }

    internal void ValidateRequiredWorkshopId(string packageId, string workshopId)
    {
        if (workshopIds.TryGetValue(workshopId, out var known)
            && (!known.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase) || known.App || known.Unpublished))
            throw new InvalidDataException("前置声明的工坊编号与数据库标识不一致或已不可用，未自动订阅：" + packageId + " / " + workshopId);
    }

    /// <summary>Only fills a locally declared dependency. Ambiguity, DLC and unavailable entries fail closed.</summary>
    public string RequiredWorkshopId(string packageId, GameVersion version)
    {
        var candidates = workshop.GetValueOrDefault(packageId, []).Where(e => !e.App && !e.Unpublished
            && e.Versions.Contains(version.Series, StringComparer.Ordinal)).ToArray();
        if (packageId.StartsWith("ludeon.rimworld", StringComparison.OrdinalIgnoreCase)
            || workshop.GetValueOrDefault(packageId, []).Any(e => e.App))
            throw new InvalidDataException("必要内容是游戏或 DLC，不能作为工坊模组订阅：" + packageId);
        if (candidates.Length != 1)
            throw new InvalidDataException(candidates.Length == 0 ? "工坊数据库未找到可用前置：" + packageId
                : "工坊数据库有多个前置候选，不能自动选择：" + packageId);
        return candidates[0].Id;
    }

    // Importing an existing order is different from choosing an automatic dependency:
    // a unique older item may be downloaded, but must not be presented as compatible.
    public (string Id, bool Compatible) ImportedWorkshopId(string packageId, GameVersion? version)
    {
        var entries = workshop.GetValueOrDefault(packageId, []);
        if (packageId.StartsWith("ludeon.rimworld", StringComparison.OrdinalIgnoreCase) || entries.Any(e => e.App))
            throw new InvalidDataException("本体或 DLC 不能从创意工坊订阅");
        var available = entries.Where(e => !e.App && !e.Unpublished).ToArray();
        var compatible = available.Where(e => version is not null && e.Versions.Contains(version.Series, StringComparer.Ordinal)).ToArray();
        var candidates = compatible.Length > 0 ? compatible : available;
        if (candidates.Length != 1)
            throw new InvalidDataException(candidates.Length == 0 ? "数据库未收录可用工坊来源，可填写 Steam 工坊链接或编号"
                : "存在多个工坊版本，请填写要订阅的 Steam 工坊链接或编号");
        return (candidates[0].Id, compatible.Length == 1);
    }

    /// <summary>Project only the enabled dependency closure. User pairs override community pairs;
    /// game content and local metadata remain hard constraints of the existing sorter.</summary>
    public IReadOnlyList<CustomOrderRule> Rules(ModCatalogue catalogue, IReadOnlyList<string> order,
        GameVersion version, IReadOnlyList<CustomOrderRule> userRules)
    {
        var active = order.Select(id => catalogue.Resolve(id, version).UniqueInstallation)
            .Where(m => m is not null).Select(m => m!).DistinctBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase).ToArray();
        var ids = active.Select(m => m.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalized = userRules.Select(r => r.Normalize()).Where(r => r.Enabled).ToArray();
        var preservation = normalized.Where(r => r.OriginalOrderIndex is not null).ToArray();
        var user = normalized.Where(r => r.OriginalOrderIndex is null).ToArray();
        var overrides = user.Where(r => r.Enabled).SelectMany(r => new[] { Key(r.Before, r.After), Key(r.After, r.Before) })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, CustomOrderRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in active.Where(m => m.Source != ModSource.Game))
        {
            if (!community.TryGetValue(mod.PackageId, out var rule)) continue;
            foreach (var target in rule.Before) Add(mod.PackageId, target);
            foreach (var target in rule.After) Add(target, mod.PackageId);
            if (rule.Top || rule.Bottom)
                foreach (var other in active.Where(m => m.Source != ModSource.Game))
                {
                    var peer = community.GetValueOrDefault(other.PackageId);
                    if (rule.Top && peer?.Top != true) Add(mod.PackageId, other.PackageId);
                    if (rule.Bottom && peer?.Bottom != true) Add(other.PackageId, mod.PackageId);
                }
        }
        foreach (var rule in user) result[Key(rule.Before, rule.After)] = rule;
        if (result.Count > 20_000) throw new InvalidDataException("当前社区关系过多，已停止自动排序。");
        return Array.AsReadOnly(result.Values.OrderBy(r => r.Before, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.After, StringComparer.OrdinalIgnoreCase).Concat(preservation).ToArray());

        void Add(string before, string after)
        {
            if (before.Equals(after, StringComparison.OrdinalIgnoreCase) || !ids.Contains(before) || !ids.Contains(after)
                || overrides.Contains(Key(before, after))) return;
            if (result.Count >= 20_000) throw new InvalidDataException("当前社区关系过多，已停止自动排序。");
            result[Key(before, after)] = new(before, after);
        }
    }

    public IReadOnlyList<CatalogueDiagnostic> Inspect(ModCatalogue catalogue, IReadOnlyList<string> order,
        GameVersion version, IReadOnlyList<CustomOrderRule> userRules)
    {
        var positions = order.Select((id, index) => (Id: id, Mod: catalogue.Resolve(id, version).UniqueInstallation, Index: index))
            .Select(x => (x.Id, Package: x.Mod?.PackageId ?? x.Id, x.Index, IsGame: x.Mod?.Source == ModSource.Game))
            .GroupBy(x => x.Package, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<CatalogueDiagnostic>();
        var users = userRules.Where(r => r.Enabled && r.OriginalOrderIndex is null).Select(r => Key(r.Before, r.After)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var violated = Rules(catalogue, order, version, userRules)
            .Where(r => r.Enabled && r.OriginalOrderIndex is null && !users.Contains(Key(r.Before, r.After)))
            .Where(r => positions.TryGetValue(r.Before, out var before) && positions.TryGetValue(r.After, out var after)
                && before.Index >= after.Index).ToArray();
        var grouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var origin = $"社区规则（{CommunityOrigin} {CommunityCommit[..Math.Min(7, CommunityCommit.Length)]}）";
        foreach (var entry in positions)
        {
            if (entry.Value.IsGame || !community.TryGetValue(entry.Key, out var rule)) continue;
            if (rule.Top) AddBoundary(entry.Key, top: true);
            if (rule.Bottom) AddBoundary(entry.Key, top: false);
        }
        foreach (var rule in violated.Where(r => !grouped.Contains(Key(r.Before, r.After))))
            diagnostics.Add(new("CommunityRuleViolated", DiagnosticSeverity.Warning,
                $"{origin}：{rule.Before} 应在 {rule.After} 之前", Identifier: positions[rule.Before].Id)
                { RelatedIdentifiers = [positions[rule.After].Id] });

        void AddBoundary(string package, bool top)
        {
            var pairs = violated.Where(r => !positions[r.Before].IsGame && !positions[r.After].IsGame).Where(r => top
                ? r.Before.Equals(package, StringComparison.OrdinalIgnoreCase) && community.GetValueOrDefault(r.After)?.Top != true
                : r.After.Equals(package, StringComparison.OrdinalIgnoreCase) && community.GetValueOrDefault(r.Before)?.Bottom != true).ToArray();
            if (pairs.Length == 0) return;
            var others = pairs.Select(r => positions[top ? r.After : r.Before].Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var pair in pairs) grouped.Add(Key(pair.Before, pair.After));
            diagnostics.Add(new(top ? "CommunityLoadTopViolated" : "CommunityLoadBottomViolated", DiagnosticSeverity.Warning,
                $"{origin}：{package} 应{(top ? "靠前" : "靠后")}加载，与 {others.Length} 个已启用模组的位置不符：{string.Join("、", others)}",
                Identifier: positions[package].Id) { RelatedIdentifiers = others });
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in positions)
            if (community.TryGetValue(entry.Key, out var rule))
                foreach (var incompatible in rule.Incompatible)
                    if (positions.TryGetValue(incompatible, out var other) && !entry.Key.Equals(incompatible, StringComparison.OrdinalIgnoreCase)
                        && seen.Add(string.Compare(entry.Key, incompatible, StringComparison.OrdinalIgnoreCase) < 0
                            ? Key(entry.Key, incompatible) : Key(incompatible, entry.Key)))
                        diagnostics.Add(new("CommunityConflict", DiagnosticSeverity.Warning,
                            $"社区冲突提示（{CommunityOrigin} {CommunityCommit[..Math.Min(7, CommunityCommit.Length)]}）：{entry.Key} 与 {incompatible}；仅为社区证据，需游戏验证。", Identifier: entry.Value.Id)
                            { RelatedIdentifiers = [other.Id] });
        return diagnostics.AsReadOnly();
    }

    private static string Key(string before, string after) => before + "\n" + after;
}
