#if DEBUG
using System.IO;
using System.Text;
using System.Windows;
using RimWise.Core;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Formats;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Game;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

/// <summary>Replay synthetic completed checkpoints through desktop publication; no AI or game runs.</summary>
internal static class MetadataReadFailurePublicationSmokeCheck
{
    internal static async Task RunAsync(MainWindow main, string output, Action<bool, string> check)
    {
        var vm = main.ViewModel;
        var root = Path.Combine(output, "metadata-read-failure-publication");
        Directory.CreateDirectory(root);
        var settings = AiSettings.Default with { Enabled = true, Model = "synthetic-publication-model" };
        new AiSettingsStore(root).Save(settings);
        var version = new GameVersion(1, 7);
        string[] original = ["fixture.readfailure", "fixture.dependent", "fixture.ordinary", "fixture.keep"];
        ModInstallation Mod(string id, ModRequirements? requirements = null) => new(
            Path.Combine(root, "Mods", id), id, ModSource.Local, "",
            new(id, "synthetic", id, ["1.7"], "", requirements ?? ModRequirements.Empty, false), []);
        var broken = Mod(original[0]) with
        {
            Metadata = null,
            RecoveredIdentity = new(original[0], original[0], "synthetic confirmed installation identity"),
            Diagnostics = [new("MetadataUnreadable", DiagnosticSeverity.Error, "合成 XML 读取失败",
                Path.Combine(root, "Mods", original[0], "About", "About.xml"), original[0])]
        };
        var catalogue = new ModCatalogue([broken,
            Mod(original[1], new([new(original[0], "读取失败的前置")], [], [], [])),
            Mod(original[2]), Mod(original[3])]);
        var scan = new ModScanResult(new(null, "", ""), version, catalogue, broken.Diagnostics);
        var paths = new GamePaths { ConfigDirectory = root, TargetVersionOverride = "1.7" };
        var format = new ModsConfigXmlFormat();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(
            "<ModsConfigData><version>1.7</version><activeMods>"
            + string.Join("", original.Select(id => "<li>" + id + "</li>")) + "</activeMods></ModsConfigData>"));
        var document = format.Read(input);
        var destination = Path.Combine(root, "ModsConfig.xml");
        void Reset()
        {
            vm.Load(document, destination);
            vm.SetCustomRules([]);
            vm.ApplyScan(scan, observeActivity: false);
            File.WriteAllBytes(destination, format.Write(document));
        }
        string[] Order() => vm.Rows.Select(row => row.PackageId).ToArray();
        string[] SavedOrder()
        {
            using var saved = File.OpenRead(destination);
            return format.Read(saved).Order.PackageIds.ToArray();
        }
        var client = new NoCallsClient();
        var sorting = new AiSortingSession(vm, root, client, new SyntheticSecrets());
        var automatic = new AutomaticSortingSession(vm, root, sorting, client);
        var limits = new MaintenanceLimits { ReadFailureTrialsEnabled = true, ConfirmModRemoval = true };
        var readTrial = new TrialExclusion(original[0], [original[0], original[1]], original,
            ["GamePatchFailure"], 1) { ReadFailureRemoval = true };
        var ordinaryTrial = new TrialExclusion(original[2], [original[2]], [original[2], original[3]],
            ["GamePatchFailure"], 2);
        string Pending(bool mixed)
        {
            var request = AiSortingSession.CreateRequest(vm, settings, null)
                with { OutputTokenLimit = limits.OutputTokensPerRequest };
            using var task = sorting.Tasks.Create(new(AiTaskKind.Maintenance, AiTaskPhase.Completed,
                AutomaticGameMaintenance.RecoveryContext(request, catalogue, request.OrderRules, version),
                original, mixed ? [original[3]] : [original[2], original[3]], DateTimeOffset.UtcNow)
            {
                Limits = limits, DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(15), Attempts = mixed ? 3 : 2,
                CandidatePendingApply = true, TrialExclusions = mixed ? [readTrial, ordinaryTrial] : [readTrial]
            });
            return task.Id;
        }
        AiTaskCheckpoint ReadCheckpoint(string id)
        {
            using var task = sorting.Tasks.Open(id);
            return task.Current;
        }
        var saves = 0;
        void Save()
        {
            File.WriteAllBytes(destination, format.Write(vm.CreateExportDocument()));
            saves++;
        }
        async Task<AutomaticMaintenanceResult> Resume(string id, Action save,
            Func<MaintenanceResult, CancellationToken, Task<bool>>? confirm = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            return await automatic.RunAsync(() => paths, settings, limits,
                new IgnoreProgress<MaintenanceProgress>(), new IgnoreProgress<OrdinaryGameProgress>(),
                new IgnoreProgress<AiResponseUpdate>(), timeout.Token, resumeId: id,
                prepareSave: () => save, confirmTrials: confirm);
        }
        void CheckNotice(AutomaticMaintenanceResult result, bool saved, string label)
        {
            var notice = AiSortingWindow.TerminalNotice(result, false);
            check(notice is not null && notice.Value.Text.Contains("排序软件读取失败")
                && notice.Value.Text.Contains("本轮游戏报错与该模组有关")
                && notice.Value.Text.Contains("安装文件保留"), label + "：明确读取失败、游戏关联及文件保留");
            check(notice is not null && (saved
                ? notice.Value.Text.Contains(MetadataReadFailureTrials.ReadFailureRemovalReason)
                : notice.Value.Text.Contains("尚未保存剔除") && !notice.Value.Text.Contains("已从排序剔除")),
                label + "：剔除状态与实际保存一致");
        }

        Reset();
        var readOnlyId = Pending(false);
        // No confirmation delegate: this would return without publication if the player were required.
        var readOnly = await Resume(readOnlyId, Save);
        check(readOnly.CandidateApplied && readOnly.Maintenance.TrialDecisionRecorded
            && readOnly.Maintenance.TrialRemovalsApproved && saves == 1,
            "readfailure-only 无玩家确认入口也自动确认并保存一次");
        check(Order().SequenceEqual([original[2], original[3]]) && SavedOrder().SequenceEqual(Order()),
            "readfailure-only 仅删除读取失败项及准确连带依赖，保存内容一致");
        var readOnlyCheckpoint = ReadCheckpoint(readOnlyId);
        check(readOnlyCheckpoint.TrialDecisionRecorded && readOnlyCheckpoint.TrialRemovalsApproved
            && !readOnlyCheckpoint.CandidatePendingApply, "readfailure-only 决定持久化且发布完成");
        CheckNotice(readOnly, true, "readfailure-only 完成提示");
        check(AiSortingWindow.TerminalNotice(readOnly, false)?.Text.Contains(original[1]) == true,
            "readfailure-only 提示列出连带依赖项");

        Reset();
        var mixedId = Pending(true);
        var confirmations = 0;
        var mixed = await Resume(mixedId, Save, (_, _) => { confirmations++; return Task.FromResult(false); });
        check(confirmations == 1 && mixed.CandidateApplied && mixed.Maintenance.TrialDecisionRecorded
            && !mixed.Maintenance.TrialRemovalsApproved && saves == 2,
            "mixed 拒绝普通试排除只询问一次，仍完成保存");
        check(Order().SequenceEqual([original[2], original[3]]) && SavedOrder().SequenceEqual(Order()),
            "mixed 保留拒绝移出的普通项，仍删除读取失败项及连带依赖");
        check(ReadCheckpoint(mixedId) is { TrialDecisionRecorded: true, TrialRemovalsApproved: false,
            CandidatePendingApply: false }, "mixed 拒绝决定持久化且完成发布");
        CheckNotice(mixed, true, "mixed 完成提示");

        Reset();
        var failedId = Pending(false);
        var saveFailed = false;
        try { await Resume(failedId, () => throw new IOException("Synthetic publication save failure")); }
        catch (IOException ex) when (ex.Message == "Synthetic publication save failure") { saveFailed = true; }
        check(saveFailed && saves == 2 && SavedOrder().SequenceEqual(original), "保存失败保留原配置文件");
        var pending = ReadCheckpoint(failedId);
        check(pending is { CandidatePendingApply: true, TrialDecisionRecorded: true, TrialRemovalsApproved: true },
            "保存失败保留已自动确认的待发布检查点");
        // Recreate the recovered editor from the unchanged on-disk original. The failed call returned no applied result.
        Reset();
        var failed = new AutomaticMaintenanceResult(new(MaintenanceOutcome.Failed, "合成保存失败，原排序已恢复",
            pending.Attempts, 0, 0, false, pending.CurrentOrder, true)
            { TrialExclusions = pending.TrialExclusions, TrialDecisionRecorded = true, TrialRemovalsApproved = true }, null, []);
        CheckNotice(failed, false, "失败恢复提示");
        check(AiSortingWindow.TerminalNotice(failed, true)?.Image == MessageBoxImage.Warning
            && !failed.CandidateApplied && Order().SequenceEqual(original), "失败恢复仍显示警告，不误报已应用或已剔除");
        var retryConfirmations = 0;
        var recovered = await Resume(failedId, Save, (_, _) => { retryConfirmations++; return Task.FromResult(false); });
        check(recovered.CandidateApplied && retryConfirmations == 0 && saves == 3
            && SavedOrder().SequenceEqual([original[2], original[3]]), "保存失败恢复复用原确认，直接保存准确删除项");
        check(!ReadCheckpoint(failedId).CandidatePendingApply, "恢复保存成功才完成待发布检查点");
        CheckNotice(recovered, true, "恢复保存完成提示");
        check(client.Calls == 0 && !Directory.Exists(Path.Combine(root, "Data", "GameTests")),
            "发布专项零 AI 调用且未创建游戏测试目录");
    }

    private sealed class IgnoreProgress<T> : IProgress<T> { public void Report(T value) { } }
    private sealed class SyntheticSecrets : ISecretStore
    {
        public string? Read(string name) => "synthetic-publication-key";
        public void Write(string name, string value) => throw new InvalidOperationException("Unexpected secret write");
        public void Delete(string name) => throw new InvalidOperationException("Unexpected secret delete");
    }
    private sealed class NoCallsClient : IAiClient
    {
        internal int Calls;
        private InvalidOperationException Unexpected() { Calls++; return new("Publication smoke must not call AI"); }
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? apiKey, CancellationToken token)
            => throw Unexpected();
        public Task<AiClientResult> ProbeModelAsync(AiRequest request, CancellationToken token) => throw Unexpected();
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token) => throw Unexpected();
    }
}
#endif
