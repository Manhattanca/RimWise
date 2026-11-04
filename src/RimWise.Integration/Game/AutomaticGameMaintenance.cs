using RimWise.Core;
using RimWise.Integration.Databases;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Safety;
using RimWise.Integration.Scanning;
using RimWise.Integration.Steam;

namespace RimWise.Integration.Game;

public sealed record AutomaticMaintenanceResult(MaintenanceResult Maintenance, ModScanResult? Scan,
    IReadOnlyList<RequiredModDownload> Subscriptions)
{
    public string DatabaseSnapshotId { get; init; } = "";
    public IReadOnlyList<string> PreparedOrder { get; init; } = [];
    public bool PreparationApplied { get; init; }
    public bool CandidateApplied { get; init; }
    public bool LastOrderDecisionRecorded { get; init; }
    public bool LastOrderKept { get; init; }
}

/// <summary>Product entry: trusted metadata preparation, then bounded AI/test iterations.
/// It never writes personal configuration or edits a player mod. The tested order remains undoable.</summary>
public static class AutomaticGameMaintenance
{
    public static string RecoveryContext(AiRequest request, ModCatalogue catalogue,
        IReadOnlyList<CustomOrderRule> rules, GameVersion version, IReadOnlyList<string>? initiallyMissing = null)
    {
        var missing = initiallyMissing ?? request.CurrentOrder.Where(id => catalogue.Resolve(id,version).Candidates.Count == 0).ToArray();
        var active = new ModCatalogue(request.CurrentOrder.Except(missing,StringComparer.OrdinalIgnoreCase).Select(id => catalogue.Resolve(id,version).UniqueInstallation
            ?? throw new InvalidDataException("原排序有未安装或重复模组。")).ToArray());
        var limited = request with { InstalledMods=request.CurrentOrder,
            // Keep the historical canonical hash projection for already-paid checkpoints.
            // Outbound facts are mapped to the actual order identifiers during preparation.
            ModRequirements=AiModRequirementFact.FromCatalogue(active), DiagnosticFacts=[] };
        return AiTaskRecoveryStore.ContextHash(limited,AiOrderPlanner.Fingerprint(request.CurrentOrder,active,request.RetainedMods,rules,version));
    }
    public static async Task<AutomaticMaintenanceResult> RunAsync(IAiClient client, AiTokenEstimator estimator,
        OrdinaryGameTestOptions options, GamePaths paths, AiRequest request, ModCatalogue catalogue,
        IReadOnlyList<CustomOrderRule> rules, MaintenanceLimits limits, IProgress<MaintenanceProgress>? progress,
        IProgress<OrdinaryGameProgress>? gameProgress, CancellationToken token, string? resumeTaskId = null,
        Func<DeclaredConflict, CancellationToken, Task<string[]?>>? chooseConflict = null, DatabaseSnapshot? database = null)
    {
        if ((database?.Id ?? "") != request.DatabaseSnapshotId)
            throw new InvalidDataException("自动任务数据库快照不匹配。");
        InsertionScope.ValidateOrder(limits.InsertionBaselineJson, request.CurrentOrder);
        var session = new PreparedGameSession(options with { UseFixedHelper=true, WarnOnSlowStartup=true, MaximumSeconds=1800,
            CollectInsertionEvidence=limits.InsertionBaselineJson is not null },paths,catalogue,request,rules,progress,gameProgress,chooseConflict,database,limits);
        var controller = new LevelOneMaintenance(client,estimator,session,resumeTaskId);
        var result = await controller.RunAsync(request,catalogue,rules,limits,progress,token);
        return new(result,session.Scan,session.Subscriptions) { DatabaseSnapshotId = database?.Id ?? "",
            PreparedOrder = session.PreparedOrder };
    }

    private sealed class PreparedGameSession(OrdinaryGameTestOptions options, GamePaths paths, ModCatalogue original,
        AiRequest initial, IReadOnlyList<CustomOrderRule> rules, IProgress<MaintenanceProgress>? progress,
        IProgress<OrdinaryGameProgress>? gameProgress,
        Func<DeclaredConflict, CancellationToken, Task<string[]?>>? chooseConflict, DatabaseSnapshot? database, MaintenanceLimits limits) : ILevelOneGameSession, IMaintenancePreparation, IMaintenanceConflictResolver, IMaintenanceTrialSession
    {
        private OrdinaryGameTestSession? inner;
        private ModCatalogue? preparedCatalogue;
        private IReadOnlyList<CustomOrderRule> preparedRules = [];
        private string[] preparedOrder = [];
        private CatalogueDiagnostic[] deferredRules = [];
        private CatalogueDiagnostic[] originalOrderAdjustments = [];
        private IReadOnlyList<string> dependencyReasons = [];
        private LocalCodePreparation? codePreparation;
        private InsertionEvidence? insertionControl;
        private readonly HashSet<string> conflictRemovals = new(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyList<string> PreparedOrder => Array.AsReadOnly(preparedOrder);
        internal ModScanResult? Scan { get; private set; }
        internal IReadOnlyList<RequiredModDownload> Subscriptions { get; private set; } = [];

        public async Task<bool> VerifySafetyAsync(CancellationToken token)
        {
            ScopedPaths.Ordinary(options.GameDirectory,true);
            ScopedPaths.Ordinary(options.PersonalConfigPath,true);
            ScopedPaths.Ordinary(options.TestRoot);
            if (!SteamGameLaunch.IsSteamInstallation(options.GameDirectory))
                throw new InvalidOperationException("当前自动维护入口需要 Steam 安装；独立安装可继续使用基础排序。");
            if (options.Version.Major!=1 || options.Version.Minor is <1 or >7)
                throw new NotSupportedException("该版本尚未适配自动游戏测试。");
            await GameTestRecovery.StopInterruptedAsync(options,token);
            if (GameProcessService.IsAnyRunning()) return false;
            TestHelperMount.CleanInterrupted(options);
            var duplicates = DuplicateInstallations.FindRequired(original, initial.CurrentOrder, options.Version);
            if (duplicates.Count > 0)
                throw new InvalidDataException(string.Join("\n\n", duplicates.Select(DuplicateInstallations.Describe)));
            return true;
        }

        public string ContextHash(AiRequest request,ModCatalogue catalogue,IReadOnlyList<CustomOrderRule> custom)
        {
            // Downloads legitimately change the inactive catalogue. Anchor recovery to the original
            // active installations and current settings/rules; every new dependency is revalidated below.
            return RecoveryContext(request,catalogue,custom,options.Version);
        }
        public string RecoveryContextHash(AiRequest request,ModCatalogue catalogue,IReadOnlyList<CustomOrderRule> custom,
            IReadOnlyList<string> initiallyMissing) => RecoveryContext(request,catalogue,custom,options.Version,initiallyMissing);

        public async Task<MaintenancePreparation> PrepareAsync(AiRequest request,ModCatalogue catalogue,
            AiTaskRecoveryStore.AiTaskLease task,CancellationToken token)
        {
            var currentCatalogue = catalogue;
            progress?.Report(new(task.Current.Attempts,"正在核对模组声明的必要前置并查找下载来源（无需 AI）…",task.Current.ReportedTokens));
            var downloads = task.Current.RequiredDownloads.ToList();
            var required = AiOrderPlanner.ExpandRequiredOrder(request.CurrentOrder, currentCatalogue, options.Version, allowMissing: true);
            WorkshopDependencyDownloads.VerifyInstalled(currentCatalogue,paths.WorkshopDirectory,options.Version,
                WorkshopDependencyDownloads.ActiveInstalledDownloads(currentCatalogue, required, options.Version, downloads));
            var dependencyResult = await RequiredDependencyPreparation.RunAsync(currentCatalogue,
                request.CurrentOrder, request.RetainedMods, options.Version, database, task.Current.ConflictApprovals,
                DownloadAsync, token, reasons =>
                {
                    dependencyReasons = reasons;
                    progress?.Report(new(task.Current.Attempts, "正在核对必要前置…", task.Current.ReportedTokens)
                        { DependencyReasons = reasons });
                });
            currentCatalogue = dependencyResult.Catalogue;
            var failures = dependencyResult.Failures;

            async Task<AutomaticDownloadResult> DownloadAsync(ModCatalogue source,
                IReadOnlyList<ImportedModDownload> pending, CancellationToken cancellation)
            {
                foreach (var item in pending)
                {
                    var intent = new RequiredModDownload(item.Identifier,item.WorkshopId)
                        { DiscoverPackageId=item.DiscoverPackageId, Reason=item.Reason };
                    var old = downloads.FirstOrDefault(d => d.PackageId.Equals(item.Identifier,StringComparison.OrdinalIgnoreCase));
                    if (old is not null && (old.WorkshopId != intent.WorkshopId || old.DiscoverPackageId != intent.DiscoverPackageId)) throw new InvalidDataException("工坊来源与恢复记录不同。");
                    if (old is null) downloads.Add(intent);
                }
                // Flush the subscription intent BEFORE Steam can change account state. Replays query
                // Steam's current state; cancellation never silently unsubscribes a completed download.
                task.Save(task.Current with { RequiredDownloads=downloads.ToArray(), Message="正在下载缺失模组／前置；恢复时将先核对 Steam 状态。" });
                Subscriptions=downloads.ToArray();
                progress?.Report(new(task.Current.Attempts,"下载缺失模组／前置："+string.Join("、",pending.Select(d=>d.DiscoverPackageId ? "工坊前置 " + d.WorkshopId : d.Identifier)),task.Current.ReportedTokens));
                var steam = new SteamWorkshopClient(AppContext.BaseDirectory);
                if (GameProcessService.IsAnyRunning()) throw new InvalidOperationException("请先关闭游戏再下载模组。");
                var downloadProgress = new Progress<string>(message => progress?.Report(new(task.Current.Attempts,message,task.Current.ReportedTokens)));
                var batch = await AutomaticModDownloads.RunAsync(pending,source,options.Version,paths.WorkshopDirectory,
                    (id,t) => steam.EnqueueSubscriptionAsync(id,downloadProgress,t),
                    (id,t) => steam.WaitForDownloadAsync(id,downloadProgress,t),cancellation);
                Scan = new(GameVersionDetector.Detect(options.GameDirectory),options.Version,batch.Catalogue,
                    CatalogueAnalysis.Inspect(batch.Catalogue,options.Version)) { ActivityRoots = new[] { paths.LocalModsDirectory, paths.WorkshopDirectory } };
                return batch;
            }
            Subscriptions=downloads.ToArray();
            currentCatalogue = database?.WithRequiredDependencies(currentCatalogue, request.CurrentOrder, options.Version) ?? currentCatalogue;
            progress?.Report(new(task.Current.Attempts,"正在启用必要前置并按社区规则排序…",task.Current.ReportedTokens));
            var all=AiOrderPlanner.ExpandRequiredOrder(request.CurrentOrder,currentCatalogue,options.Version,allowMissing:true);
            all=DeclaredConflictResolution.Replay(currentCatalogue,all,request.RetainedMods,options.Version,task.Current.ConflictApprovals);
            if (MissingModResolution.Find(currentCatalogue,all,request.RetainedMods,options.Version,failures) is { } missingConflict)
            {
                var approval = await ChooseAndRecordAsync(missingConflict,task,chooseConflict,token);
                all=all.Except(approval.RemovedIds,StringComparer.OrdinalIgnoreCase).ToArray();
            }
            ValidateInstallations(currentCatalogue,all,options.Version);
            // Existing approvals were replayed above, before missing-item resolution.
            while (DeclaredConflictResolution.Find(currentCatalogue,all,request.RetainedMods,options.Version) is { } conflict)
            {
                var approval = await ChooseAndRecordAsync(conflict,task,chooseConflict,token);
                all=all.Except(approval.RemovedIds,StringComparer.OrdinalIgnoreCase).ToArray();
            }
            if (task.Current.PendingConflict is { } pendingConflict)
            {
                var conflict = RuntimeConflictResolution.Restore(pendingConflict, currentCatalogue, all, request.RetainedMods, options.Version);
                var approval = await ChooseAndRecordAsync(conflict, task, chooseConflict, token);
                all = all.Except(approval.RemovedIds, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            conflictRemovals.UnionWith(task.Current.ConflictApprovals.SelectMany(a => a.RemovedIds));
            var effectiveRules = InsertionScope.Rules(database?.Rules(currentCatalogue,all,options.Version,rules) ?? rules, limits.InsertionBaselineJson);
            progress?.Report(new(task.Current.Attempts,"正在本地分析程序集关系并核对拓扑顺序（无需 AI）…",task.Current.ReportedTokens));
            codePreparation = await Task.Run(() => LocalCodeTopology.Prepare(currentCatalogue, all, options.Version, options.TestRoot, token), token);
            var sorted=new LoadOrderSorter().Sort(currentCatalogue,all,options.Version,options.Version,effectiveRules,
                deferAdvisoryCycles: true, preferredOrder: codePreparation.PreferredOrder);
            if(!sorted.Complete) throw new InvalidDataException(sorted.Explanation + (database is null ? "" : $" 社区规则来源：{database.CommunityOrigin} {database.CommunityCommit[..7]}；自定义规则与本地依赖同时参与。"));
            preparedOrder = sorted.Order.ToArray();
            InsertionScope.ValidateOrder(limits.InsertionBaselineJson, preparedOrder,
                task.Current.ConflictApprovals.SelectMany(a => a.RemovedIds));
            deferredRules = sorted.Diagnostics.Where(d => d.Code == "OrderingCycleDeferred").ToArray();
            var added = preparedOrder.Except(request.CurrentOrder,StringComparer.OrdinalIgnoreCase).ToArray();
            var preparationMessage = $"规则／拓扑排序完成：{preparedOrder.Length} 项，补齐 {added.Length} 个必要前置";
            if (added.Length > 0) preparationMessage += "（" + string.Join("、",added.Take(12)) + (added.Length > 12 ? "…" : "") + "）";
            originalOrderAdjustments = sorted.Diagnostics.Where(d => d.Code == "InsertionOrderAdjusted").ToArray();
            var adjusted = originalOrderAdjustments.Select(d => d.Message).Distinct().ToArray();
            if (adjusted.Length > 0)
                preparationMessage += $"；按模组需求调整 {adjusted.Length} 处原有顺序（原因见本轮回报）";
            var deferred = sorted.Diagnostics.Where(d => d.Code == "OrderingCycleDeferred").Select(d => d.Message).Distinct().ToArray();
            preparationMessage += deferred.Length == 0 ? "；前置和排序验证通过后才启动测试。"
                : "；" + string.Join("；", deferred);
            task.Save(task.Current with { Message = preparationMessage });
            progress?.Report(new(task.Current.Attempts,preparationMessage,task.Current.ReportedTokens)
                { PreparedOrder = Array.AsReadOnly(preparedOrder),
                    ReportText = adjusted.Length == 0 ? null : "原有顺序按需求调整：\n" + string.Join("\n", adjusted) });
            await PrepareInsertionControlAsync(currentCatalogue, task, token);
            inner=new(options,currentCatalogue,request.CurrentOrder,request.RetainedMods,effectiveRules,gameProgress,
                approvedRemovals: task.Current.ConflictApprovals.SelectMany(a => a.RemovedIds).ToArray());
            preparedCatalogue = currentCatalogue;
            preparedRules = effectiveRules;
            // Level one can only reorder this closure. Do not send the whole inactive library or database.
            var activeCatalogue = new ModCatalogue(all.Select(id => currentCatalogue.Resolve(id,options.Version).UniqueInstallation!).ToArray());
            return new(request with { InstalledMods=all, OrderRules=effectiveRules,
                DiagnosticFacts=request.DiagnosticFacts.Concat(deferredRules.Select(d => new AiDiagnosticFact(d.Code, d.Identifier ?? ""))).Distinct().ToArray(),
                ModRequirements=AiModRequirementFact.FromCatalogue(activeCatalogue,all,options.Version) },currentCatalogue,sorted.Order) { Rules=effectiveRules };
        }
        public async Task BeginAsync(CancellationToken token)
        {
            await (inner??throw new InvalidOperationException()).BeginAsync(token);
            OrdinaryGameRunner.WriteNew(ScopedPaths.Child(inner.SessionDirectory!,"preparation.json"),new
            {
                Schema=1, CallsAI=false, DatabaseSnapshotId=database?.Id ?? "", OriginalOrder=initial.CurrentOrder,
                PreparedOrder=preparedOrder, AddedPrerequisites=preparedOrder.Except(initial.CurrentOrder,StringComparer.OrdinalIgnoreCase).ToArray(),
                MetadataDependenciesChecked=true, DependencyReasons=dependencyReasons, TopologyValidated=true, RuleCount=preparedRules.Count,
                DeferredOrderingRules=deferredRules, OriginalOrderAdjustments=originalOrderAdjustments,
                LocalCodeAnalysis=codePreparation is null ? null : new
                { codePreparation.Report.AssemblyCount, codePreparation.Report.CacheHits, codePreparation.Report.Limited,
                    FactCount=codePreparation.Report.Facts.Count }
            });
        }
        private async Task PrepareInsertionControlAsync(ModCatalogue catalogue, AiTaskRecoveryStore.AiTaskLease task, CancellationToken token)
        {
            if (limits.InsertionBaselineJson is null) return;
            insertionControl = task.Current.InsertionControl;
            if (insertionControl is not null || task.Current.InsertionControlAttempts > 0) return;
            var removed = task.Current.ConflictApprovals.SelectMany(a => a.RemovedIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var baseline = InsertionScope.Read(limits.InsertionBaselineJson).Where(id => !removed.Contains(id)).ToArray();
            // Leave at least one permitted run for the candidate. An interrupted control is not replayed silently.
            if (baseline.Length == 0 || limits.AttemptLimitReached(task.Current.Attempts + 1)
                || baseline.Any(id => catalogue.Resolve(id, options.Version).UniqueInstallation is null)) return;
            var control = new OrdinaryGameTestSession(options, catalogue, baseline, baseline, [], gameProgress);
            if (!await control.VerifySafetyAsync(token)) throw new InvalidOperationException("游戏正在运行，无法开始原排序对照。");
            task.Save(task.Current with { InsertionControlAttempts = 1, Message = "正在测试原排序，记录已有错误作为新增模式的对照。" });
            progress?.Report(new(task.Current.Attempts, "正在测试原排序作为对照（不调用 AI，计入测试次数）…", task.Current.ReportedTokens));
            MaintenanceGameReport report;
            try
            {
                await control.BeginAsync(token);
                report = await control.TestAsync(baseline, token);
            }
            finally { await control.FinishAsync(false, CancellationToken.None); }
            insertionControl = report.InsertionEvidence ?? new(false, [], []);
            task.Save(task.Current with { InsertionControl = insertionControl, Message = insertionControl.Completed
                ? "原排序对照完成，接下来测试新增 Mod。" : "原排序未完成全部测试；新增排序仍按严格无红标准检查。" });
        }

        public async Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order,CancellationToken token)
        {
            InsertionScope.ValidateOrder(limits.InsertionBaselineJson, order,
                conflictRemovals);
            var report = await inner!.TestAsync(order,token);
            var originalIds = InsertionScope.Read(limits.InsertionBaselineJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newIds = order.Where(id => !originalIds.Contains(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in newIds.ToArray())
            {
                var packageId = preparedCatalogue?.Resolve(id, options.Version).UniqueInstallation?.PackageId;
                if (!string.IsNullOrWhiteSpace(packageId)) newIds.Add(packageId);
            }
            // A known new owner always prevents an old-error exemption, even if its text matches.
            var newOwner = report.DiagnosticFacts.Any(f => newIds.Contains(f.PackageId));
            return report with { ExistingErrorsOnly = limits.InsertionBaselineJson is not null && !newOwner
                && report.Errors.Count > 0 && InsertionEvidence.Matches(insertionControl, report.InsertionEvidence) };
        }
        public Task ApplyAsync(AiOrderPlan plan,CancellationToken token)
        {
            InsertionScope.ValidateOrder(limits.InsertionBaselineJson, plan.After,
                conflictRemovals);
            return inner!.ApplyAsync(plan,token);
        }
        public Task FinishAsync(bool accept,CancellationToken token) => inner?.FinishAsync(accept,token)??Task.CompletedTask;
        public void AuthorizeTrialExclusions(IReadOnlyList<TrialExclusion> trials) => inner!.AuthorizeTrialExclusions(trials);

        public async Task<AiOrderPlan?> ResolveAsync(StructuredAiResponse response, MaintenanceGameReport report,
            IReadOnlyList<string> current, AiTaskRecoveryStore.AiTaskLease task, CancellationToken token)
        {
            if (preparedCatalogue is null || inner is null) throw new InvalidOperationException("测试尚未准备。");
            var approval = await ResolveRuntimeConflictAsync(response, report, preparedCatalogue, current,
                initial.RetainedMods, options.Version, task, chooseConflict, token);
            if (approval is null) return null;
            var remaining = current.Except(approval.RemovedIds, StringComparer.OrdinalIgnoreCase).ToArray();
            var sorted = new LoadOrderSorter().Sort(preparedCatalogue, remaining, options.Version, options.Version, preparedRules, deferAdvisoryCycles: true);
            var plan = AiOrderPlanner.PreparedOrder(current, sorted.Order, preparedCatalogue, initial.RetainedMods,
                options.Version, preparedRules, [approval]);
            if (!sorted.Complete || !plan.CanApply) throw new InvalidDataException("冲突选择后的排序未通过复核。");
            inner.AuthorizeConflictRemovals(task.Current.ConflictApprovals);
            conflictRemovals.UnionWith(task.Current.ConflictApprovals.SelectMany(a => a.RemovedIds));
            return plan;
        }
    }

    internal static async Task<ConflictRemovalApproval?> ResolveRuntimeConflictAsync(StructuredAiResponse response,
        MaintenanceGameReport report, ModCatalogue catalogue, IReadOnlyList<string> current,
        IReadOnlyList<string> retained, GameVersion version, AiTaskRecoveryStore.AiTaskLease task,
        Func<DeclaredConflict, CancellationToken, Task<string[]?>>? choose, CancellationToken token)
    {
        // No model removal action is made valid by attaching a plausible conflict group.
        if (response.Actions.Count != 0) return null;
        var conflict = response.Conflicts.Select(suggestion => RuntimeConflictResolution.FromReport(suggestion, report,
            catalogue, current, retained, version)).FirstOrDefault(candidate => candidate is not null);
        if (conflict is null) return null;
        // Persist the safe projection of the paid result before UI; resume needs no repeat HTTP request.
        task.Save(task.Current with { PendingConflict = conflict.RuntimeEvidence,
            Message = "正在等待玩家选择组合冲突的保留与移出项。" });
        return await ChooseAndRecordAsync(conflict, task, choose, token);
    }

    internal static async Task<string[]> ResolveConflictsAsync(ModCatalogue catalogue, string[] expandedOrder,
        IReadOnlyList<string> retained, GameVersion version, AiTaskRecoveryStore.AiTaskLease task,
        Func<DeclaredConflict, CancellationToken, Task<string[]?>>? choose, CancellationToken token)
    {
        var current = DeclaredConflictResolution.Replay(catalogue, expandedOrder, retained, version, task.Current.ConflictApprovals);
        while (DeclaredConflictResolution.Find(catalogue, current, retained, version) is { } conflict)
        {
            token.ThrowIfCancellationRequested();
            var approval = await ChooseAndRecordAsync(conflict, task, choose, token);
            current = current.Except(approval.RemovedIds, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return current;
    }

    internal static async Task<ConflictRemovalApproval> ChooseAndRecordAsync(DeclaredConflict conflict,
        AiTaskRecoveryStore.AiTaskLease task, Func<DeclaredConflict, CancellationToken, Task<string[]?>>? choose,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var automatic = task.Current.Limits?.ConfirmModRemoval == false;
        if (!automatic && choose is null) throw new MaintenancePauseException(conflict.Reason + " 任务已暂停，等待玩家选择。");
        var selected = automatic ? AutomaticRemovalChoice.Select(conflict) : await choose!(conflict, token);
        // Retention forbids automatic removal, but never traps the player in an
        // unresolvable dialog. An explicit choice is still required to override it.
        if (automatic && selected is null && choose is not null)
        {
            automatic = false;
            selected = await choose(conflict, token);
        }
        token.ThrowIfCancellationRequested();
        if (selected is null) throw new MaintenancePauseException(automatic
            ? "没有可自动移出的安全选项；原有模组、保留项及其必要前置继续受到保护。"
            : "用户停止冲突处理，未再启动游戏或请求 AI。");
        var preview = ConflictChoicePreview.Create(conflict, selected, allowRetainedRemoval: !automatic);
        if (!preview.CanContinue) throw new InvalidDataException("冲突选择无效：" + preview.Message);
        var approval = preview.Approve(conflict);
        task.Save(task.Current with { PendingConflict = null, ConflictApprovals = task.Current.ConflictApprovals.Append(approval).ToArray(),
            Message = automatic ? "已按自动移出设置记录处理；仅移出排序，安装文件保留，等待复测。"
                : "已记录玩家选择；仅移出启用排序，安装文件保留，等待复测。" });
        return approval;
    }

    private static void ValidateInstallations(ModCatalogue catalogue,IReadOnlyList<string> order,GameVersion version)
    {
        foreach(var id in order)
        {
            if(catalogue.Resolve(id,version).UniqueInstallation is null)
                throw new InvalidDataException("模组未安装或存在重复安装："+id);
        }
    }
}
