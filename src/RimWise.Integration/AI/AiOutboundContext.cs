using RimWise.Core;

namespace RimWise.Integration.AI;

public sealed record AiDiagnosticFact(string Code, string PackageId);
public sealed record AiMetadataWarning(string PackageId, string Field, int OmittedItems);
public sealed record AiModRequirementFact(string PackageId, IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> LoadBefore, IReadOnlyList<string> LoadAfter, IReadOnlyList<string> IncompatibleWith)
{
    public static AiModRequirementFact[] FromCatalogue(ModCatalogue catalogue,
        IReadOnlyList<string>? order = null, GameVersion? version = null)
    {
        var installationIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packageIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in order ?? [])
        {
            if (catalogue.Resolve(id, version).UniqueInstallation is not { } mod) continue;
            installationIds.TryAdd(mod.Directory, id);
            if (mod.PackageId.Length > 0) packageIds.TryAdd(mod.PackageId, id);
        }
        // Facts must use the same identifiers as CurrentOrder, including Workshop _steam aliases.
        // Otherwise the outbound known-ID filter discards the canonical owner's requirements.
        string Identifier(string package) => packageIds.GetValueOrDefault(package, package);
        return catalogue.Installations.Where(mod => mod.Metadata is not null && mod.PackageId.Length > 0).Select(mod =>
        {
            var requirements = mod.Metadata!.Requirements;
            return new AiModRequirementFact(installationIds.GetValueOrDefault(mod.Directory, mod.PackageId),
                requirements.Dependencies.Select(dep => Identifier(dep.PackageId)).ToArray(),
                requirements.LoadBefore.Select(Identifier).ToArray(), requirements.LoadAfter.Select(Identifier).ToArray(),
                requirements.IncompatibleWith.Select(Identifier).ToArray());
        }).ToArray();
    }
}

/// <summary>Only this projection enters a sorting prompt. Log excerpts are selected and sanitized locally.</summary>
internal sealed record AiOutboundContext(string GameVersion, IReadOnlyList<string> CurrentOrder,
    IReadOnlyList<string> InstalledMods, IReadOnlyList<string> RetainedMods,
    IReadOnlyList<AiDiagnosticFact> Diagnostics, IReadOnlyList<AiModRequirementFact> ModRequirements,
    IReadOnlyList<CustomOrderRule> OrderRules, AiStartupContext? Startup, AiRuntimeContext? Runtime)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AiLogContext? Logs { get; init; }
    public IReadOnlyList<AiMetadataWarning> MetadataWarnings { get; init; } = [];
    public int MetadataWarningCount { get; init; }
    public string RequirementScope => "CurrentOrder and recursive dependencies; optional relations only within this scope. Inactive unrelated metadata and records with no declared relations omitted. This does not prove absence of runtime dependencies.";
    private const int MaximumItems = 20_000;
    private static readonly HashSet<string> DiagnosticCodes = new(StringComparer.Ordinal)
    {
        "DuplicateInstallation", "MetadataUnreadable", "VersionNotDeclared", "TargetVersionUnknown",
        "DependencyNotInstalled", "OrderModMissing", "OrderModAmbiguous", "DependencyNotResolvedInOrder",
        "DuplicateOrderEntry", "DuplicateActiveInstallation", "DependencyLoadsAfter", "LoadBeforeViolated",
        "LoadAfterViolated", "DeclaredConflict", "OrderingCycle", "OrderingCycleDeferred", "CustomRuleViolated", "GameStartupFailed", "GameLogError",
        "GameContentLoadsAfter", "GameMissingXmlParent", "GameTypeLoadFailure", "GamePatchFailure",
        "GameInitializationFailure", "GameHarmonyFailure", "GameTestBlockingFailure"
    };

    internal static AiOutboundContext Create(AiRequest request)
    {
        var warnings = new List<AiMetadataWarning>();
        var warningCount = 0;
        string[] MetadataIds(string owner, string field, IReadOnlyList<string>? values)
        {
            var valid = values is null || values.Count > MaximumItems ? []
                : values.Where(AiModIdentifier.IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var invalid = values is null ? 1 : values.Count > MaximumItems ? values.Count
                : values.Count(id => !AiModIdentifier.IsValid(id));
            if (invalid > 0)
            {
                warningCount++;
                if (warnings.Count < 100) warnings.Add(new(owner, field, invalid));
            }
            return valid;
        }
        var current = Identifiers(request.CurrentOrder, "CurrentOrder");
        var installed = MetadataIds("", "InstalledMods", request.InstalledMods);
        var retained = Identifiers(request.RetainedMods, "RetainedMods");
        var known = current.Concat(installed).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var facts = Bounded(request.DiagnosticFacts).Where(fact => fact.Code is not null && DiagnosticCodes.Contains(fact.Code)
            && fact.PackageId is not null && (fact.PackageId.Length == 0 || known.Contains(fact.PackageId)))
            .Distinct().ToArray();
        var metadata = Bounded(request.ModRequirements);
        var scope = current.Concat(retained).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byOwner = metadata.ToLookup(fact => fact.PackageId, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(scope);
        while (pending.TryDequeue(out var owner))
            foreach (var fact in byOwner[owner])
                foreach (var dependency in fact.Dependencies is { Count: <= MaximumItems } dependencies ? dependencies : [])
                    if (AiModIdentifier.IsValid(dependency) && known.Contains(dependency) && scope.Add(dependency))
                        pending.Enqueue(dependency);
        var requirements = metadata.Where(fact => known.Contains(fact.PackageId) && scope.Contains(fact.PackageId))
            .Select(fact => new AiModRequirementFact(fact.PackageId, MetadataIds(fact.PackageId, "Dependencies", fact.Dependencies),
                MetadataIds(fact.PackageId, "LoadBefore", fact.LoadBefore).Where(scope.Contains).ToArray(),
                MetadataIds(fact.PackageId, "LoadAfter", fact.LoadAfter).Where(scope.Contains).ToArray(),
                MetadataIds(fact.PackageId, "IncompatibleWith", fact.IncompatibleWith).Where(scope.Contains).ToArray()))
            .Where(fact => fact.Dependencies.Count + fact.LoadBefore.Count + fact.LoadAfter.Count + fact.IncompatibleWith.Count > 0)
            .DistinctBy(fact => (fact.PackageId, string.Join('\n', fact.Dependencies), string.Join('\n', fact.LoadBefore),
                string.Join('\n', fact.LoadAfter), string.Join('\n', fact.IncompatibleWith))).ToArray();
        var rules = Bounded(request.OrderRules).Where(rule => rule.Enabled && known.Contains(rule.Before)
            && known.Contains(rule.After)).Select(rule => new CustomOrderRule(rule.Before, rule.After)
                { Required = rule.Required, OriginalOrderIndex = rule.OriginalOrderIndex }).Distinct().ToArray();
        return new(RimWise.Core.GameVersion.Parse(request.GameVersion)?.ToString() ?? "unknown", current, installed,
            retained.Where(known.Contains).ToArray(), facts, requirements, rules, request.StartupContext?.CopyValidated(),
            request.RuntimeContext?.CopyValidated() is { } runtime ? runtime with
            { Observations = runtime.Observations.Where(o => o.PackageId.Length == 0 || known.Contains(o.PackageId)).ToArray() } : null)
        {
            MetadataWarnings = warnings,
            MetadataWarningCount = warningCount,
            Logs = request.LogContext?.CopyValidated() is { } logs ? logs with
                { Owners = logs.Owners.Where(o => known.Contains(o.PackageId)).ToArray(),
                  Files = logs.Files.Where(f => known.Contains(f.PackageId)).ToArray(),
                  Assemblies = logs.Assemblies.Where(f => current.Contains(f.PackageId, StringComparer.OrdinalIgnoreCase)
                    && current.Contains(f.RelatedPackageId, StringComparer.OrdinalIgnoreCase)).ToArray(),
                  Code = logs.Code.Where(f => current.Contains(f.PackageId, StringComparer.OrdinalIgnoreCase)).ToArray() } : null
        };
    }

    private static string[] Identifiers(IReadOnlyList<string> values, string field)
    {
        var result = Bounded(values);
        var invalid = Array.FindIndex(result, id => !AiModIdentifier.IsValid(id));
        if (invalid >= 0)
            throw new ArgumentException($"{field} 第 {invalid + 1} 项模组标识无效（空值、超过 512 字符、控制字符或路径）。请检查对应模组元数据；原排序已保留。");
        return result;
    }

    private static T[] Bounded<T>(IReadOnlyList<T> values)
    {
        if (values is null || values.Count > MaximumItems || values.Any(value => value is null))
            throw new ArgumentException("AI 上下文无效或超过数量限制，已停止发送。");
        return values.ToArray();
    }
}
