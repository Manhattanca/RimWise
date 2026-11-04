namespace RimWise.Core;

public sealed record LoadOrderSortResult(
    IReadOnlyList<string> Order,
    IReadOnlyList<CatalogueDiagnostic> Diagnostics,
    bool Changed,
    bool Complete,
    string? FailureReason = null)
{
    public string Explanation => !Complete ? FailureReason ?? "当前清单无法安全自动排序，请先处理诊断。"
        : Diagnostics.Any(d => d.Code == "InsertionOrderAdjusted") ? "已按模组需求调整相关原有模组的位置，其余尽量保留原顺序。"
        : Diagnostics.Any(d => d.Code == "OrderingCycleDeferred") ? "已按可执行的规则整理排序；循环内的先后建议暂不处理，保留必要前置，继续游戏测试。"
        : Changed ? $"已按依赖和模组声明调整顺序，共 {Order.Count} 项。"
        : "当前顺序已经符合可识别的依赖和前后置声明。";
}

/// <summary>Stable, explainable topological sort for the current order only.</summary>
public sealed class LoadOrderSorter
{
    public LoadOrderSortResult Sort(ModCatalogue catalogue, IReadOnlyList<string> currentOrder,
        GameVersion? configurationVersion, GameVersion? targetVersion, IReadOnlyList<CustomOrderRule>? customRules = null,
        bool deferAdvisoryCycles = false, IReadOnlyList<string>? preferredOrder = null)
        => Evaluate(catalogue, currentOrder, configurationVersion, targetVersion, customRules,
            deferAdvisoryCycles, preferredOrder, validateOnly: false);

    /// <summary>Check graph constraints without treating a different valid tie order as a violation.</summary>
    public LoadOrderSortResult Validate(ModCatalogue catalogue, IReadOnlyList<string> currentOrder,
        GameVersion? configurationVersion, GameVersion? targetVersion, IReadOnlyList<CustomOrderRule>? customRules = null,
        bool deferAdvisoryCycles = false)
        => Evaluate(catalogue, currentOrder, configurationVersion, targetVersion, customRules,
            deferAdvisoryCycles, null, validateOnly: true);

    private static LoadOrderSortResult Evaluate(ModCatalogue catalogue, IReadOnlyList<string> currentOrder,
        GameVersion? configurationVersion, GameVersion? targetVersion, IReadOnlyList<CustomOrderRule>? customRules,
        bool deferAdvisoryCycles, IReadOnlyList<string>? preferredOrder, bool validateOnly)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(currentOrder);
        var original = currentOrder.ToArray();
        var diagnostics = CatalogueAnalysis.InspectSelectedOrder(catalogue, original, configurationVersion, targetVersion).ToList();
        var blockers = diagnostics.Where(issue => issue.Code is
            "OrderModMissing" or "OrderModAmbiguous" or "DuplicateOrderEntry" or
            "DuplicateActiveInstallation" or "DependencyNotInstalled" or
            "DependencyNotResolvedInOrder" or "DeclaredConflict" or "MetadataUnreadable" or "TargetVersionUnknown").ToArray();
        if (blockers.Length > 0)
            return new(original, diagnostics.AsReadOnly(), false, false,
                "自动排序未执行：" + string.Join("；", blockers.Take(3).Select(issue => issue.Message)));
        if (configurationVersion is not null && targetVersion is not null && configurationVersion.Series != targetVersion.Series)
            return new(original, diagnostics.AsReadOnly(), false, false, "排序文件与目标游戏版本不同，请先确认目标版本后再自动排序。");

        var positions = original.Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);
        var activeRules = (customRules ?? []).Select(r => r.Normalize()).Where(r => r.Enabled).ToArray();
        var preservation = activeRules.Where(r => r.OriginalOrderIndex is not null)
            .OrderBy(r => r.OriginalOrderIndex).ThenBy(r => r.Before, StringComparer.OrdinalIgnoreCase).ToArray();
        // A local code graph may break ties only between currently eligible vertices.
        // It never introduces required dependencies or overrides declared/community edges.
        var preferred = (preferredOrder ?? original).Concat(original).Where(positions.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var baselinePositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in preservation)
        {
            baselinePositions.TryAdd(rule.Before, rule.OriginalOrderIndex!.Value);
            baselinePositions.TryAdd(rule.After, rule.OriginalOrderIndex.Value + 1);
        }
        // Candidate/model/code-analysis order is not evidence that an original needs
        // moving. Restore baseline tie preferences; graph constraints justify movement.
        var baselineQueue = new Queue<string>(preferred.Where(baselinePositions.ContainsKey)
            .OrderBy(id => baselinePositions[id]));
        var preferences = preferred.Select(id => baselinePositions.ContainsKey(id) ? baselineQueue.Dequeue() : id)
            .Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index, StringComparer.OrdinalIgnoreCase);
        // Metadata may spell a package ID with different casing. Graph vertices must
        // retain the active order's exact identifiers so repeated validation is stable.
        var canonicalIdentifiers = original.ToDictionary(id => id, StringComparer.OrdinalIgnoreCase);
        var resolvedIdentifiers = original.ToDictionary(id => id,
            id => catalogue.Resolve(id, configurationVersion).UniqueInstallation,
            StringComparer.OrdinalIgnoreCase);
        var packageIdentifiers = resolvedIdentifiers
            .Where(item => item.Value is not null && item.Value.PackageId.Length > 0)
            .GroupBy(item => item.Value!.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Key).ToArray(), StringComparer.OrdinalIgnoreCase);
        var edges = original.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var requiredEdges = original.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var indegree = original.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var identifier in original)
        {
            var installation = resolvedIdentifiers[identifier];
            var requirements = installation?.Metadata?.Requirements;
            if (requirements is null) continue;
            AddTargets(requirements.Dependencies.Select(dependency => dependency.PackageId), identifier, before: true, required: true);
            AddTargets(requirements.LoadAfter, identifier, before: true);
            AddTargets(requirements.LoadBefore, identifier, before: false);
        }

        foreach (var (before, after) in GameContentOrder.Pairs(catalogue, original, configurationVersion))
        {
            requiredEdges[before].Add(after);
            if (edges[before].Add(after)) indegree[after]++;
        }

        foreach (var rule in activeRules.Where(r => r.OriginalOrderIndex is null))
        {
            foreach (var from in ResolveOrderIdentifiers(rule.Before))
                foreach (var to in ResolveOrderIdentifiers(rule.After))
                {
                    if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) continue;
                    if (rule.Required) requiredEdges[from].Add(to);
                    if (edges[from].Add(to)) indegree[to]++;
                }
        }

        var deferredDiagnostics = new List<CatalogueDiagnostic>();
        if (deferAdvisoryCycles)
        {
            foreach (var group in OrderingCycleFallback.DeferAdvisoryEdges(original, edges, requiredEdges))
            {
                var names = string.Join("、", group.Take(12));
                if (group.Length > 12) names += $"…（共 {group.Length} 项）";
                var message = $"先后规则存在循环，暂不处理组内矛盾建议：{names}。保留必要前置要求，其余尽量沿用当前顺序，继续游戏测试。";
                deferredDiagnostics.AddRange(group.Select(id => new CatalogueDiagnostic(
                    "OrderingCycleDeferred", DiagnosticSeverity.Warning, message, Identifier: id)));
            }
        }

        var adjustedDiagnostics = new List<CatalogueDiagnostic>();
        foreach (var rule in preservation)
            foreach (var from in ResolveOrderIdentifiers(rule.Before))
                foreach (var to in ResolveOrderIdentifiers(rule.After))
                {
                    if (edges[from].Contains(to)) continue;
                    // Actual dependencies, official order, declarations and user/community
                    // constraints take precedence. Never create a cycle to freeze a baseline.
                    if (Reaches(to, from))
                    {
                        adjustedDiagnostics.Add(new("InsertionOrderAdjusted", DiagnosticSeverity.Information,
                            $"为满足模组前置或明确先后要求，调整原有顺序：{to} 在 {from} 之前；其他位置尽量保持。", Identifier: from));
                    }
                    else edges[from].Add(to);
                }
        foreach (var id in original) indegree[id] = 0;
        foreach (var targets in edges.Values)
            foreach (var to in targets) indegree[to]++;

        var dependencyPriorities = new Dictionary<string, int>(preferences, StringComparer.OrdinalIgnoreCase);
        if (preservation.Length > 0)
        {
            // Finish an earlier original's prerequisite chain before unrelated later
            // rows. Otherwise a newly inserted prerequisite at the end could push its
            // consumer behind every unrelated mod, despite only one needed correction.
            var pendingDegrees = new Dictionary<string, int>(indegree, StringComparer.OrdinalIgnoreCase);
            var pendingReady = new Queue<string>(original.Where(id => pendingDegrees[id] == 0));
            var topological = new List<string>(original.Length);
            while (pendingReady.TryDequeue(out var id))
            {
                topological.Add(id);
                foreach (var to in edges[id])
                    if (--pendingDegrees[to] == 0) pendingReady.Enqueue(to);
            }
            foreach (var id in topological.AsEnumerable().Reverse())
                foreach (var to in edges[id])
                    dependencyPriorities[id] = Math.Min(dependencyPriorities[id], dependencyPriorities[to]);
        }
        var ready = new SortedSet<string>(Comparer<string>.Create((left, right) =>
        {
            var compare = dependencyPriorities[left].CompareTo(dependencyPriorities[right]);
            if (compare == 0) compare = preferences[left].CompareTo(preferences[right]);
            if (compare == 0) compare = positions[left].CompareTo(positions[right]);
            return compare != 0 ? compare : StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }));
        foreach (var id in original.Where(id => indegree[id] == 0)) ready.Add(id);
        var sorted = new List<string>(original.Length);
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            sorted.Add(id);
            foreach (var target in edges[id])
                if (--indegree[target] == 0) ready.Add(target);
        }

        if (sorted.Count != original.Length)
        {
            var cycle = FindCycle(original.Where(id => indegree[id] > 0), edges);
            var path = cycle.Count <= 12 ? string.Join(" → ", cycle.Append(cycle[0]))
                : string.Join(" → ", cycle.Take(12)) + $" → …（共 {cycle.Count} 项）";
            var reason = $"循环排序规则：{path}。请检查这些模组的前置或先后声明。";
            diagnostics.AddRange(cycle.Select(id => new CatalogueDiagnostic("OrderingCycle", DiagnosticSeverity.Error, reason, Identifier: id)));
            return new(original, diagnostics.AsReadOnly(), false, false, reason);
        }
        if (validateOnly)
        {
            var violated = edges.SelectMany(pair => pair.Value.Where(to => positions[pair.Key] >= positions[to])
                .Select(to => new CatalogueDiagnostic("OrderConstraintViolated", DiagnosticSeverity.Error,
                    $"{pair.Key} 必须在 {to} 之前加载。", Identifier: pair.Key))).ToArray();
            var checkedDiagnostics = diagnostics.Concat(deferredDiagnostics).Concat(adjustedDiagnostics).Concat(violated).ToArray();
            return new(original, checkedDiagnostics, false, violated.Length == 0,
                violated.Length == 0 ? null : string.Join("；", violated.Take(3).Select(d => d.Message)));
        }
        var changed = !sorted.SequenceEqual(original, StringComparer.Ordinal);
        var finalDiagnostics = CatalogueAnalysis.InspectSelectedOrder(catalogue, sorted, configurationVersion, targetVersion)
            .Concat(deferredDiagnostics).Concat(adjustedDiagnostics).ToArray();
        return new(sorted.AsReadOnly(), finalDiagnostics, changed, true);

        void AddTargets(IEnumerable<string> targets, string identifier, bool before, bool required = false)
        {
            foreach (var target in targets)
            {
                foreach (var targetIdentifier in ResolveOrderIdentifiers(target))
                {
                    var from = before ? targetIdentifier : identifier;
                    var to = before ? identifier : targetIdentifier;
                    // An installation cannot load before itself, even when its metadata
                    // uses a different spelling/source alias for its own package ID.
                    if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) continue;
                    if (required) requiredEdges[from].Add(to);
                    if (edges[from].Add(to)) indegree[to]++;
                }
            }
        }

        IEnumerable<string> ResolveOrderIdentifiers(string target)
        {
            if (canonicalIdentifiers.TryGetValue(target, out var identifier)) return [identifier];
            return packageIdentifiers.TryGetValue(target, out var identifiers) ? identifiers : [];
        }

        bool Reaches(string start, string target)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
            var pending = new Stack<string>();
            pending.Push(start);
            while (pending.TryPop(out var id))
            {
                if (id.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var next in edges[id])
                    if (seen.Add(next)) pending.Push(next);
            }
            return false;
        }
    }

    // Iterative DFS avoids consuming the call stack for large collections. Downstream blocked
    // nodes are not labelled as members of a cycle unless a path actually returns to them.
    private static IReadOnlyList<string> FindCycle(IEnumerable<string> unresolved,
        IReadOnlyDictionary<string, HashSet<string>> edges)
    {
        var colors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        var stack = new Stack<(string Id, IEnumerator<string> Targets)>();
        try
        {
            foreach (var root in unresolved)
            {
                if (colors.ContainsKey(root)) continue;
                Push(root);
                while (stack.Count > 0)
                {
                    var frame = stack.Peek();
                    if (!frame.Targets.MoveNext())
                    {
                        stack.Pop().Targets.Dispose();
                        colors[frame.Id] = 2;
                        path.RemoveAt(path.Count - 1);
                        continue;
                    }
                    var next = frame.Targets.Current;
                    if (!colors.TryGetValue(next, out var color)) Push(next);
                    else if (color == 1)
                    {
                        var start = path.FindIndex(id => id.Equals(next, StringComparison.OrdinalIgnoreCase));
                        return path.Skip(start).ToArray();
                    }
                }
            }
        }
        finally { foreach (var frame in stack) frame.Targets.Dispose(); }
        throw new InvalidOperationException("Unresolved graph did not contain a cycle.");

        void Push(string id)
        {
            colors[id] = 1;
            path.Add(id);
            stack.Push((id, edges[id].GetEnumerator()));
        }
    }
}
