#if DEBUG
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Views;
using RimWise.Desktop.Services;
using RimWise.Desktop.Presentation;
using RimWise.Formats;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Databases;
using RimWise.Integration.Game;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

/// <summary>Exercises the request lifecycle using synthetic data; never contacts an API or starts a game.</summary>
internal static class AiSortingSmokeCheck
{
    internal static void Attach(MainWindow main, string output)
    {
        main.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try { await RunAsync(main, output); Application.Current.Shutdown(0); }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                main.ViewModel.MarkExported();
                Application.Current.Shutdown(1);
            }
        };
    }

    private static async Task RunAsync(MainWindow main, string output)
    {
        var checks = new List<string>();
        var vm = main.ViewModel;
        var root = Path.Combine(output, "自动排序 中文 [样例] & café '");
        Directory.CreateDirectory(root);
        var settings = AiSettings.Default with { Enabled = true, Model = "synthetic-model" };
        var store = new AiSettingsStore(root);
        store.Save(settings);
        ModInstallation Mod(string id) => new(Path.Combine(root, id), id, ModSource.Local, "",
            new(id, "合成作者", id, ["1.7"], "", ModRequirements.Empty, false), []);
        var catalogue = new ModCatalogue([Mod("first.mod"), Mod("second.mod")]);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(
            "<ModsConfigData><version>1.7</version><activeMods><li>first.mod</li><li>second.mod</li></activeMods></ModsConfigData>"));
        var document = new ModsConfigXmlFormat().Read(input);
        var scan = new ModScanResult(new(null, "", ""), GameVersion.Parse("1.7"), catalogue, []);
        void Reset()
        {
            vm.Load(document, Path.Combine(root, "ModsConfig.xml"));
            vm.ApplyScan(scan, observeActivity: false); vm.SetCustomRules([]); store.Save(settings);
        }
        string[] Order() => vm.Rows.Select(row => row.PackageId).ToArray();
        void Unchanged() => Assert(Order().SequenceEqual(document.Order.PackageIds), "Request changed editor before confirmation.");
        static AiClientResult Reply(params AiAction[] actions) => new(true, "合成响应", new(1, "排序说明", actions, []), null)
            { Usage = new(120, 30, 150) };
        AiClientResult GoodReply() => Reply(new AiAction("move", "second.mod", 1, false, null, "测试移动"));
        var secrets = new SecretCounter();
        var client = new FakeClient { Response = (_, _) => Task.FromResult(GoodReply()) };
        AiSortingWindow Open() { var w = new AiSortingWindow(vm, root, client: client, secrets: secrets) { Owner = main }; w.Show(); return w; }
        Reset();
        new AutomaticSortingPreferencesStore(UserStorage.DirectoryPath).AcknowledgeNotice();
        main.AiSortingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var singleton = main.OwnedWindows.OfType<AiSortingWindow>().Single();
        Assert(ReferenceEquals(singleton, main.OpenAiSorting()) && singleton.Title == "自动排序", "Main button did not open a single AI sorting window.");
        singleton.Close();
        checks.Add("main button opens independent singleton window");

        var window = Open();
        try
        {
            Assert(client.Calls == 0 && secrets.Reads == 0, "Opening window sent a request or read a key.");
            Assert(window.ReadMaintenanceLimits() is { OutputTokensPerRequest: 16384, ExpandOutputBudget: true },
                "New automatic task lost its reasoning-aware output allowance.");
            checks.Add("new automatic tasks use 16384 output allowance with adaptive expansion");
            window.PermissionBox.SelectedIndex = 1;
            Assert(store.Load().PermissionLevel == AiPermissionLevel.Level1OrderAndRequiredDependencies
                && new AutomaticSortingPreferencesStore(root).Load().Mode == "full"
                && ((ComboBoxItem)window.PermissionBox.SelectedItem).Content.ToString() == "AI 全部重排序",
                "Full sorting mode was not saved or labelled correctly.");
            window.PermissionBox.SelectedIndex = 2;
            Assert(store.Load().PermissionLevel == AiPermissionLevel.Level3CodeAdaptation && !window.AutomaticButton.IsEnabled
                && ((ComboBoxItem)window.PermissionBox.SelectedItem).Content.ToString()!.Contains("（未完成）"), "Level 3 label or guard missing.");
            window.PermissionBox.SelectedIndex = 0;
            Assert(store.Load() == settings && new AutomaticSortingPreferencesStore(root).Load().Mode == "insert"
                && client.Calls == 0 && secrets.Reads == 0, "Mode selection sent a request or lost API settings.");
            checks.Add("insertion first and full sorting second persist; adaptation stays blocked; mode changes make no AI call");
            window.EstimateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(client.Calls == 0 && secrets.Reads == 0 && window.TokenText.Text.Length > 0, "Local estimate used network or key.");
            checks.Add("opening and estimating incur no request and no secret access");
            var historical = Path.Combine(root, "Data", "GameTests", "game-" + Guid.NewGuid().ToString("N"));
            var historicalAttempt = Path.Combine(historical, "attempt-01 测试数据");
            Directory.CreateDirectory(historicalAttempt);
            File.WriteAllText(Path.Combine(historical, "finished.json"), "{\"ProcessStopped\":true}");
            File.WriteAllText(Path.Combine(historicalAttempt, "Player.log"), "historical log");
            Assert(window.ClearLogsButton.IsEnabled && window.ClearLogsButton.Content.ToString() == "清理历史日志", "Clear logs entry missing");
            window.ClearLogsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!window.ClearLogsButton.IsEnabled && !window.RecoveryButton.IsEnabled, "Cleanup did not guard concurrent actions");
            var cleanupDeadline = DateTime.UtcNow.AddSeconds(20);
            while (window.IsWorking && DateTime.UtcNow < cleanupDeadline) await Task.Delay(20);
            Assert(!window.IsWorking && !File.Exists(Path.Combine(historicalAttempt, "Player.log"))
                && window.StatusText.Text.Contains("已清理") && client.Calls == 0 && secrets.Reads == 0, "Cleanup failed or called AI");
            checks.Add("clear historical logs in background without AI; task controls guarded during cleanup");
            await window.StartSortingAsync(); Unchanged();
            Assert(client.Calls == 1 && secrets.Reads == 1 && window.ApplyButton.IsEnabled, "Validated result is unavailable.");
            Assert(window.TokenText.Text.Contains("150") && window.AcceptedList.Items.Count == 1, "Inline result or actual usage missing.");
            Assert(window.TokenText.Text.Contains("累计") && new AiSortingSession(vm, root, client, secrets).UsageDescription().Contains("150"),
                "Usage was not persisted and available after reopening.");
            var first = window.CurrentOutcome!;
            window.ApplyResult(first);
            Assert(Order()[0] == "second.mod" && vm.HasChanges && !window.ApplyButton.IsEnabled, "Apply failed or stayed reusable.");
            vm.UndoCommand.Execute(null); Unchanged();
            vm.RedoCommand.Execute(null); Assert(Order()[0] == "second.mod", "Cancel undo failed.");
            foreach (var choice in new[] { MessageBoxResult.Cancel, MessageBoxResult.No })
            {
                var answer = main.Dispatcher.InvokeAsync(() =>
                {
                    var dialog = main.OwnedWindows.OfType<MessageDialogWindow>().Single();
                    var buttons = dialog.ButtonsPanel.Children.Cast<Button>().ToArray();
                    Assert(buttons.Single(button => button.IsDefault).Tag is MessageBoxResult.Cancel,
                        "Update prompt must default to keeping the app open.");
                    buttons.Single(button => Equals(button.Tag, choice)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }, DispatcherPriority.ApplicationIdle);
                Assert(main.ConfirmUpdateEdits() == (choice == MessageBoxResult.No), "Update unsaved choice ignored.");
                await answer;
                Assert(vm.HasChanges && Order()[0] == "second.mod", "Update prompt altered unsaved order.");
            }
            checks.Add("update cancellation retains unsaved order; discard allows exit; default choice is cancel");
            Assert(!File.Exists(Path.Combine(root, "ModsConfig.xml")), "AI wrote game configuration.");
            checks.Add("inline results, usage, one-step apply, undo and cancel undo; no game write");
            Reset(); await window.StartSortingAsync();
            var stale = window.CurrentOutcome!;
            vm.ClearOrderCommand.Execute(null);
            Assert(!window.ApplyButton.IsEnabled, "Manual edit did not invalidate result immediately.");
            MustReject(() => window.ApplyResult(stale));
            checks.Add("manual editing invalidates old result immediately");
            Reset(); await window.StartSortingAsync(); stale = window.CurrentOutcome!;
            vm.ApplyScan(scan with { Catalogue = new(catalogue.Installations.ToArray()) }, observeActivity: false);
            MustReject(() => window.ApplyResult(stale));
            checks.Add("refreshed catalogue invalidates result");
            Reset(); await window.StartSortingAsync(); stale = window.CurrentOutcome!;
            store.Save(settings with { Model = "changed-model" });
            MustReject(() => window.ApplyResult(stale)); Unchanged();
            checks.Add("changed settings rejected again at application boundary");

            Reset();
            client.Response = (_, _) => Task.FromResult(Reply(new AiAction("patch", "second.mod", null, false, "outside", "不允许")));
            await window.StartSortingAsync(); Unchanged();
            Assert(!window.ApplyButton.IsEnabled && window.RejectedList.Items.Count > 0, "Rejected operation can be applied.");
            checks.Add("invalid file operations remain visible and cannot apply");

            Reset(); client.Response = (_, _) => Task.FromResult(GoodReply() with { Succeeded = false });
            await window.StartSortingAsync(); Unchanged();
            Assert(!window.ApplyButton.IsEnabled, "Unsuccessful service response can apply.");
            checks.Add("unsuccessful response cannot apply even with attached actions");

            Reset(); client.Response = (_, _) => Task.FromResult(GoodReply());
            var auditPath = Path.Combine(root, "Data", "ai-audit.json");
            var auditBytes = File.ReadAllBytes(auditPath);
            File.WriteAllText(auditPath, "invalid synthetic audit");
            try
            {
                await window.StartSortingAsync(); Unchanged();
                Assert(!window.ApplyButton.IsEnabled && window.AcceptedList.Items.Count == 1 && window.TokenText.Text.Contains("审计"),
                    "Audit failure hid billing/result evidence or allowed application.");
            }
            finally { File.WriteAllBytes(auditPath, auditBytes); }
            checks.Add("audit failure preserves display and disables application");

            Reset();
            var session = new AiSortingSession(vm, root, client, secrets);
            var automatic = new AutomaticSortingSession(vm, root, session, client);
            var paths = new GamePaths();
            var request = AiSortingSession.CreateRequest(vm, settings, null);
            var revision = vm.ContentRevision;
            var candidate = new AutomaticMaintenanceResult(new(MaintenanceOutcome.Passed, "合成地图通过",
                2, 1, 150, false, ["second.mod", "first.mod"], true), null, []);
            var callsBeforePublish = client.Calls;
            File.WriteAllText(auditPath, "invalid synthetic audit");
            try
            {
                try
                {
                    automatic.PublishCandidate(candidate, request, settings, () => paths, paths,
                        revision, vm.CurrentCatalogue!, CancellationToken.None);
                    throw new InvalidOperationException("Automatic result applied without an audit record.");
                }
                catch (JsonException) { }
                Unchanged();
            }
            finally { File.WriteAllBytes(auditPath, auditBytes); }
            var applied = automatic.PublishCandidate(candidate, request, settings, () => paths, paths,
                revision, vm.CurrentCatalogue!, CancellationToken.None);
            Assert(applied.Maintenance.Message.Contains("可撤销") && Order()[0] == "second.mod"
                && client.Calls == callsBeforePublish, "Tested candidate was not published locally.");
            vm.UndoCommand.Execute(null); Unchanged();
            Assert(!File.Exists(Path.Combine(root, "ModsConfig.xml")), "Automatic publish wrote personal configuration.");
            Reset(); request = AiSortingSession.CreateRequest(vm, settings, null); revision = vm.ContentRevision;
            vm.ClearOrderCommand.Execute(null);
            automatic.PublishCandidate(candidate, request, settings, () => paths, paths,
                revision, catalogue, CancellationToken.None);
            Assert(vm.Rows.Count == 0, "Automatic candidate overwrote a concurrent manual edit.");
            checks.Add("automatic tested candidate requires audit, applies as one undoable edit, preserves concurrent edits and sends no request");

            Reset();
            request = AiSortingSession.CreateRequest(vm, settings, null); revision = vm.ContentRevision;
            var trial = new TrialExclusion("first.mod", ["first.mod"], ["first.mod", "second.mod"], ["GamePatchFailure"], 1);
            var trialCandidate = candidate with { Maintenance = candidate.Maintenance with
                { LastAttemptedOrder = new[] { "second.mod" }, TrialExclusions = new[] { trial } } };
            var auditBeforeTrial = File.ReadAllBytes(auditPath); var trialSaves = 0;
            automatic.PublishCandidate(trialCandidate, request, settings, () => paths, paths, revision,
                vm.CurrentCatalogue!, default, () => trialSaves++);
            Unchanged();
            Assert(trialSaves == 0 && File.ReadAllBytes(auditPath).SequenceEqual(auditBeforeTrial),
                "Unconfirmed trial published or saved a removal.");
            automatic.PublishCandidate(trialCandidate with { Maintenance = trialCandidate.Maintenance with
                { TrialRemovalsApproved = true } }, request, settings, () => paths, paths, revision,
                vm.CurrentCatalogue!, default, () => trialSaves++);
            Assert(Order().SequenceEqual(["second.mod"]) && trialSaves == 1 && client.Calls == callsBeforePublish,
                "Approved trial was not applied locally.");
            vm.UndoCommand.Execute(null); Unchanged();
            checks.Add("trial exclusions cannot publish without final confirmation; approved removal is one undoable edit");

            Reset();
            var conflictCatalogue = new ModCatalogue(catalogue.Installations.Select(mod => mod.PackageId == "first.mod"
                ? mod with { Metadata = mod.Metadata! with { Requirements = new([], [], [], ["second.mod"]) } } : mod).ToArray());
            vm.ApplyScan(scan with { Catalogue = conflictCatalogue });
            string[] retainedOrder = ["first.mod", "second.mod"];
            var insertionScope = InsertionScope.Capture(retainedOrder);
            var insertionRules = InsertionScope.Rules([], insertionScope);
            request = AiSortingSession.CreateRequest(vm, settings, null) with { RetainedMods = retainedOrder, OrderRules = insertionRules };
            revision = vm.ContentRevision;
            var declared = DeclaredConflictResolution.Find(conflictCatalogue, retainedOrder, retainedOrder, GameVersion.Parse("1.7")!)!;
            var approvedPreview = ConflictChoicePreview.Create(declared, ["first.mod"], allowRetainedRemoval: true);
            Assert(approvedPreview.CanContinue, "Retained conflict did not allow explicit removal");
            var approved = new ConflictRemovalApproval("first.mod", approvedPreview.RemovedIds.ToArray())
                { SelectedIds = ["first.mod"], RetentionOverrideIds = approvedPreview.RetentionOverrideIds.ToArray() };
            var retainedCandidate = candidate with { Maintenance = candidate.Maintenance with
                { LastAttemptedOrder = new[] { "second.mod" }, ConflictApprovals = new[] { approved }, InsertionBaselineJson = insertionScope } };
            var retainedSaves = 0;
            automatic.PublishCandidate(retainedCandidate, request, settings, () => paths, paths, revision,
                vm.CurrentCatalogue!, default, () => retainedSaves++);
            Assert(Order().SequenceEqual(["second.mod"]) && retainedSaves == 1 && client.Calls == callsBeforePublish,
                "Explicit retention override was blocked at publication/save or requested AI again.");
            vm.UndoCommand.Execute(null); Unchanged();
            vm.ApplyScan(scan);
            checks.Add("explicit protected conflict removal publishes and saves in insertion mode; undo restores the full order");

            Reset();
            paths = new GamePaths { ConfigDirectory = Path.Combine(root, "游戏配置 中文 &") };
            var limits = new MaintenanceLimits();
            request = AiSortingSession.CreateRequest(vm, settings, null) with { OutputTokenLimit = limits.OutputTokensPerRequest };
            revision = vm.ContentRevision;
            var originalCatalogue = vm.CurrentCatalogue!;
            var refreshedScan = scan with { Catalogue = new(catalogue.Installations.ToArray()) };
            string pendingId;
            using (var task = session.Tasks.Create(new(AiTaskKind.Maintenance, AiTaskPhase.Completed,
                AutomaticGameMaintenance.RecoveryContext(request, originalCatalogue, [], GameVersion.Parse("1.7")!),
                request.CurrentOrder.ToArray(), ["second.mod", "first.mod"], DateTimeOffset.UtcNow)
                { Limits = limits, DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(15), CandidatePendingApply = true }))
                pendingId = task.Id;
            candidate = candidate with { Scan = refreshedScan, Maintenance = candidate.Maintenance with { TaskId = pendingId } };
            callsBeforePublish = client.Calls;
            try
            {
                automatic.PublishCandidate(candidate, request, settings, () => paths, paths,
                    revision, originalCatalogue, CancellationToken.None, () => throw new IOException("合成保存失败"));
                throw new InvalidOperationException("Save failure was hidden.");
            }
            catch (IOException ex) when (ex.Message == "合成保存失败") { }
            Assert(Order()[0] == "second.mod" && ReferenceEquals(vm.CurrentCatalogue, refreshedScan.Catalogue)
                && session.Tasks.Inspect().Single(t => t.Id == pendingId).Checkpoint!.CandidatePendingApply,
                "Scan publication or failed save lost the tested candidate.");
            var appliedRevision = vm.ContentRevision;
            var saves = 0;
            var resumed = await automatic.RunAsync(() => paths, settings, limits, new Progress<MaintenanceProgress>(),
                new Progress<OrdinaryGameProgress>(), new Progress<AiResponseUpdate>(), default, pendingId,
                prepareSave: () => () => saves++);
            Assert(resumed.Maintenance.Outcome == MaintenanceOutcome.Passed && saves == 1
                && vm.ContentRevision == appliedRevision && client.Calls == callsBeforePublish
                && !session.Tasks.Inspect().Single(t => t.Id == pendingId).Checkpoint!.CandidatePendingApply,
                "Pending save recovery duplicated the edit/request or failed to complete.");
            vm.UndoCommand.Execute(null); Unchanged();
            checks.Add("downloaded catalogue applies, save failure retains candidate, exact-target recovery saves without duplicate editing or requests");

            Reset();
            var originalPreparation = new[] { "first.mod", "second.mod", "third.mod", "ludeon.rimworld" };
            vm.Load(document.WithOrder(new(originalPreparation)), Path.Combine(root, "ModsConfig.xml"));
            var dependent = Mod("first.mod");
            dependent = dependent with { Metadata = dependent.Metadata! with { Requirements = new(
                [new("dependency.mod", "base"), new("ludeon.rimworld.biotech", "Biotech")], [], [], []) } };
            var dependency = Mod("dependency.mod");
            dependency = dependency with { Metadata = dependency.Metadata! with { Requirements = new([new("base.mod", "recursive")], [], [], []) } };
            var preparationCatalogue = new ModCatalogue([dependent, Mod("second.mod"), Mod("third.mod"), dependency,
                Mod("base.mod"), Mod("ludeon.rimworld") with { Source = ModSource.Game },
                Mod("ludeon.rimworld.biotech") with { Source = ModSource.Game }, Mod("unrelated.mod")]);
            vm.ApplyScan(scan with { Catalogue = preparationCatalogue }, observeActivity: false);
            request = AiSortingSession.CreateRequest(vm, settings, null) with { OutputTokenLimit = limits.OutputTokensPerRequest };
            revision = vm.ContentRevision;
            var preparedOrder = new LoadOrderSorter().Sort(preparationCatalogue,
                AiOrderPlanner.ExpandRequiredOrder(originalPreparation, preparationCatalogue, GameVersion.Parse("1.7")!),
                GameVersion.Parse("1.7"), GameVersion.Parse("1.7")).Order.ToArray();
            var temporaryTrial = new TrialExclusion("first.mod", ["first.mod"], preparedOrder, ["GameLogError"], 1);
            var unsuccessful = new AutomaticMaintenanceResult(new(MaintenanceOutcome.Stopped, "合成排障尚未通过；已恢复原排序",
                2, 1, 150, false, preparedOrder.Where(id => id != "first.mod").ToArray(), true)
                { TrialExclusions = [temporaryTrial] }, null, []) { PreparedOrder = preparedOrder };
            callsBeforePublish = client.Calls;
            var publishedPreparation = automatic.PublishPreparation(unsuccessful, request, settings, () => paths,
                paths, revision, preparationCatalogue, default);
            Assert(publishedPreparation.PreparationApplied && !publishedPreparation.CandidateApplied
                && originalPreparation.All(Order().Contains) && Order().Contains("dependency.mod")
                && Order().Contains("base.mod") && Order().Contains("ludeon.rimworld.biotech")
                && !Order().Contains("unrelated.mod") && client.Calls == callsBeforePublish
                && !File.Exists(Path.Combine(root, "ModsConfig.xml")),
                "Failed tests hid installed mandatory prerequisites, removed trial mods, billed AI or saved personal configuration.");
            Assert(Array.IndexOf(Order(), "base.mod") < Array.IndexOf(Order(), "dependency.mod")
                && Array.IndexOf(Order(), "dependency.mod") < Array.IndexOf(Order(), "first.mod"), "Recursive dependency order was lost.");
            window.ShowAutomaticResult(publishedPreparation);
            var stoppedStatus = window.StatusText.Text;
            window.RefreshConfiguration();
            Assert(window.StatusText.Text == stoppedStatus && !stoppedStatus.Contains("已排序完成")
                && window.AcceptedList.Items.Count == preparedOrder.Length,
                "Activation overwrote stopped status or displayed a successful completion.");
            var preparationCheckpoint = new AiTaskCheckpoint(AiTaskKind.Maintenance, AiTaskPhase.Paused,
                AutomaticGameMaintenance.RecoveryContext(request, preparationCatalogue, [], GameVersion.Parse("1.7")!),
                originalPreparation, unsuccessful.Maintenance.LastAttemptedOrder.ToArray(), DateTimeOffset.UtcNow) { Attempts = 2 };
            var visibleRequest = AiSortingSession.CreateRequest(vm, settings, null) with { OutputTokenLimit = limits.OutputTokensPerRequest };
            Assert(AutomaticSortingSession.RestorePreparationRequest(visibleRequest, preparationCheckpoint,
                preparationCatalogue, [], GameVersion.Parse("1.7")!, null).CurrentOrder.SequenceEqual(originalPreparation),
                "Visible preparation lost the original recovery anchor.");
            MustReject(() => AutomaticSortingSession.RestorePreparationRequest(visibleRequest with { CurrentOrder = ["first.mod"] },
                preparationCheckpoint, preparationCatalogue, [], GameVersion.Parse("1.7")!, null));
            CustomOrderRule[] cycleRules = [new("second.mod", "third.mod"), new("third.mod", "second.mod")];
            var cycleCheckpoint = preparationCheckpoint with { ContextHash = AutomaticGameMaintenance.RecoveryContext(
                request, preparationCatalogue, cycleRules, GameVersion.Parse("1.7")!) };
            Assert(AutomaticSortingSession.RestorePreparationRequest(visibleRequest, cycleCheckpoint,
                preparationCatalogue, cycleRules, GameVersion.Parse("1.7")!, null).CurrentOrder.SequenceEqual(originalPreparation),
                "Deferred advisory cycle blocked recovery of the visible preparation.");
            checks.Add("automatic preparation recovery accepts deferred cycles while preserving the original context");
            // A cancelled or stale run cannot republish its preparation over the user's edits.
            var afterPreparationRevision = vm.ContentRevision;
            automatic.PublishPreparation(unsuccessful, request, settings, () => paths, paths, revision, preparationCatalogue, default);
            Assert(vm.ContentRevision == afterPreparationRevision, "Stale preparation edited the order twice.");
            vm.UndoCommand.Execute(null);
            Assert(Order().SequenceEqual(originalPreparation), "Preparation cannot be undone in one step.");
            var cancelledPreparation = automatic.PublishPreparation(unsuccessful with { Maintenance = unsuccessful.Maintenance with
                { Outcome = MaintenanceOutcome.Cancelled } }, request, settings, () => paths, paths, vm.ContentRevision, preparationCatalogue, default);
            Assert(!cancelledPreparation.PreparationApplied && Order().SequenceEqual(originalPreparation), "Cancelled preparation published changes.");
            checks.Add("unsuccessful tests retain recursive installed dependencies and declared DLC as one undoable unsaved edit, preserve all original mods, reject stale/cancelled results and safely normalize recovery without AI");

            foreach (var stop in new[] { MaintenanceOutcome.Cancelled, MaintenanceOutcome.Failed, MaintenanceOutcome.Stopped })
            {
                Reset();
                var simpleRequest = AiSortingSession.CreateRequest(vm, settings, null);
                var last = new AutomaticMaintenanceResult(new(stop, "合成测试停止，原排序已恢复", 2, 1, 300, false,
                    ["second.mod", "first.mod"], true), null, []);
                var stoppedSaves = 0;
                automatic.RememberStoppedOrder(last, simpleRequest, settings, () => paths, paths, vm.ContentRevision,
                    vm.CurrentCatalogue!, () => stoppedSaves++);
                Assert(automatic.LastOrderChoice(last)?.After.SequenceEqual(last.Maintenance.LastAttemptedOrder) == true,
                    "Stopped task lost its last order.");
                var restoredResult = automatic.ResolveLastOrder(last, false);
                Assert(restoredResult.LastOrderDecisionRecorded && !restoredResult.LastOrderKept && stoppedSaves == 0,
                    "Declining a stopped candidate saved it.");
                Unchanged();
                automatic.RememberStoppedOrder(last, simpleRequest, settings, () => paths, paths, vm.ContentRevision,
                    vm.CurrentCatalogue!, () => stoppedSaves++);
                var keptResult = automatic.ResolveLastOrder(last, true);
                Assert(keptResult.LastOrderKept && !keptResult.CandidateApplied && stoppedSaves == 1
                    && Order().SequenceEqual(last.Maintenance.LastAttemptedOrder), "Stopped choice lost order or claimed test success.");
                vm.UndoCommand.Execute(null); Unchanged();
                automatic.RememberStoppedOrder(last, simpleRequest, settings, () => paths, paths, vm.ContentRevision, vm.CurrentCatalogue!);
                vm.SetCustomRules([]);
                MustReject(() => automatic.ResolveLastOrder(last, true));
                Assert(!AutomaticSortingSession.NeedsLastOrderChoice(last.Maintenance with { Attempts = 0 })
                    && !AutomaticSortingSession.NeedsLastOrderChoice(last.Maintenance with { OriginalRestored = false }),
                    "An unsafe or absent last order offered publication.");
            }
            checks.Add("cancelled, failed, token/attempt stopped tasks offer keep/save or restore; one-step undo, stale edits and unsafe recovery protected");
            window.ResponseText.Clear();
            window.ApplyMaintenanceProgress(new(2, "服务格式不兼容，改用本地试排除", 0)
                { ReportText = "本轮未收到 AI 最终方案", PreparedOrder = ["second.mod", "first.mod"] });
            Assert(window.ResponseText.Text.Contains("本轮未收到 AI 最终方案") && window.AcceptedList.Items.Count == 2,
                "Round feedback or full current order missing.");
            window.ShowAutomaticResult(new(new(MaintenanceOutcome.Cancelled, "已取消", 2, 1, 0, true,
                ["second.mod", "first.mod"], true), null, []));
            Assert(window.ResponseText.Text.Contains("本地试排除") && window.ResponseText.Text.Contains("未收到"),
                "Terminal result erased round feedback.");
            checks.Add("round reports display format failures and complete current order; cancellation retains readable report");
            Reset();
            vm.Load(document.WithOrder(new(originalPreparation)), Path.Combine(root, "ModsConfig.xml"));
            vm.ApplyScan(new(new(null, "", ""), GameVersion.Parse("1.7"), preparationCatalogue, []), observeActivity: false);
            request = AiSortingSession.CreateRequest(vm, settings, null);
            revision = vm.ContentRevision;

            // A completed saved trial can be published from the visible preparation, without rebilling.
            automatic.PublishPreparation(unsuccessful, request, settings, () => paths, paths, vm.ContentRevision, preparationCatalogue, default);
            var testedPreparation = preparedOrder.Where(id => id != "first.mod").ToArray();
            string preparedTrialId;
            using (var task = session.Tasks.Create(preparationCheckpoint with { Phase = AiTaskPhase.Completed,
                CurrentOrder = testedPreparation, Limits = limits, DeadlineUtc = DateTimeOffset.MaxValue,
                TrialExclusions = [temporaryTrial], CandidatePendingApply = true }))
                preparedTrialId = task.Id;
            var preparationSaves = 0;
            var recoveredPreparation = await automatic.RunAsync(() => paths, settings, limits, new Progress<MaintenanceProgress>(),
                new Progress<OrdinaryGameProgress>(), new Progress<AiResponseUpdate>(), default, preparedTrialId,
                prepareSave: () => () => preparationSaves++, confirmTrials: (_, _) => Task.FromResult(true));
            Assert(recoveredPreparation.CandidateApplied && preparationSaves == 1 && Order().SequenceEqual(testedPreparation)
                && recoveredPreparation.Maintenance.Message.Contains("已排序完成") && client.Calls == callsBeforePublish,
                "Prepared baseline recovery lost the successful trial or completion status.");
            window.ShowAutomaticResult(recoveredPreparation);
            var completedStatus = window.StatusText.Text;
            window.RefreshConfiguration();
            Assert(window.StatusText.Text == completedStatus && completedStatus.Contains("已排序完成")
                && window.SummaryText.Text.Contains("未检测到红字"), "Successful completion was hidden by reactivation.");
            vm.ClearOrderCommand.Execute(null);
            Assert(!window.StatusText.Text.Contains("已排序完成") && window.StatusText.Text.Contains("已变化"),
                "Manual edits retained the completion claim for an outdated order.");
            checks.Add("visible preparation resumes a completed trial, confirms removal and saves once without AI; completion persists on reactivation and invalidates after editing");
            Reset();

            void ResetTrial()
            {
                Reset();
                vm.Load(document.WithOrder(new(["first.mod", "second.mod", "third.mod"])),
                    Path.Combine(root, "ModsConfig.xml"));
                var first = Mod("first.mod");
                first = first with { Metadata = first.Metadata! with { Requirements = new([new("dependency.mod", "dependency")], [], [], []) } };
                vm.ApplyScan(scan with { Catalogue = new([first, Mod("second.mod"), Mod("third.mod"), Mod("dependency.mod")]) }, observeActivity: false);
            }
            string[] testedTrial = ["second.mod", "dependency.mod", "third.mod"];
            string[] keptTrial = ["second.mod", "dependency.mod", "first.mod", "third.mod"];
            var reorderedTrial = trial with { TestedOrder = ["dependency.mod", "third.mod", "first.mod", "second.mod"] };
            foreach (var outcome in new[] { MaintenanceOutcome.Cancelled, MaintenanceOutcome.Failed, MaintenanceOutcome.Stopped })
            foreach (var stoppedPhase in outcome == MaintenanceOutcome.Cancelled ? new[] { AiTaskPhase.Cancelled }
                : new[] { AiTaskPhase.Paused, AiTaskPhase.ReplySaved, AiTaskPhase.Applying })
            foreach (var confirmRemoval in new[] { true, false })
            foreach (var approveRemoval in new[] { false, true })
            foreach (var insertion in new[] { false, true })
            foreach (var keepOrder in new[] { true, false })
            {
                ResetTrial();
                var stoppedRequest = AiSortingSession.CreateRequest(vm, settings, null);
                var baseline = insertion ? JsonSerializer.Serialize(new[] { "second.mod", "third.mod" }) : null;
                string stoppedTaskId;
                using (var task = session.Tasks.Create(new(AiTaskKind.Maintenance, stoppedPhase,
                    AutomaticGameMaintenance.RecoveryContext(stoppedRequest, vm.CurrentCatalogue!, [], GameVersion.Parse("1.7")!),
                    stoppedRequest.CurrentOrder.ToArray(), testedTrial, DateTimeOffset.UtcNow)
                    { Limits = limits with { InsertionBaselineJson = baseline }, DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(15),
                        Attempts = 2, Requests = 1, ReportedTokens = 150, TrialExclusions = [reorderedTrial] }))
                {
                    stoppedTaskId = task.Id;
                    MustRejectCheckpoint(() => task.ValidateSave(task.Current with { TrialDecisionRecorded = true }));
                    MustRejectCheckpoint(() => task.ValidateSave(task.Current with { TrialRemovalsApproved = true }));
                    MustRejectCheckpoint(() => task.ValidateSave(task.Current with
                        { StoppedOrderDecision = "restore", TrialDecisionRecorded = true }));
                    MustRejectCheckpoint(() => task.ValidateSave(task.Current with
                        { StoppedOrderDecision = "keep", TrialDecisionRecorded = true, MayApply = true }));
                    MustRejectCheckpoint(() => task.ValidateSave(task.Current with
                        { StoppedOrderDecision = "keep", TrialDecisionRecorded = true, Phase = AiTaskPhase.Testing }));
                }
                var stoppedTrial = new AutomaticMaintenanceResult(new(outcome, "合成停止", 2, 1, 150, false, testedTrial, true)
                {
                    TaskId = stoppedTaskId,
                    TrialExclusions = [reorderedTrial],
                    InsertionBaselineJson = baseline
                }, null, []);
                var stoppedTrialSaves = 0;
                automatic.RememberStoppedOrder(stoppedTrial, stoppedRequest, settings, () => paths, paths, vm.ContentRevision,
                    vm.CurrentCatalogue!, () => stoppedTrialSaves++, confirmModRemoval: confirmRemoval);
                Assert(automatic.NeedsStoppedRemovalConfirmation(stoppedTrial) == confirmRemoval,
                    "Stopped removal confirmation ignored task setting.");
                Assert(automatic.LastOrderChoice(stoppedTrial)!.After.SequenceEqual(confirmRemoval ? keptTrial : testedTrial),
                    "Default stopped preview silently authorized trial removal.");
                var stoppedResult = automatic.ResolveLastOrder(stoppedTrial, keepOrder, approveRemoval);
                using var savedStoppedTask = session.Tasks.Open(stoppedTaskId);
                var savedStopped = savedStoppedTask.Current;
                Assert(savedStopped.Phase == stoppedPhase && savedStopped.Finished && !savedStopped.CandidatePendingApply
                    && savedStopped.StoppedOrderDecision == (keepOrder ? "keep" : "restore")
                    && savedStopped.Attempts == 2 && savedStopped.Requests == 1 && savedStopped.ReportedTokens == 150,
                    "Stopped choice was not persisted or changed test outcome/usage history.");
                if (!keepOrder)
                {
                    Assert(stoppedTrialSaves == 0 && Order().SequenceEqual(stoppedRequest.CurrentOrder)
                        && !savedStopped.TrialDecisionRecorded && !savedStopped.TrialRemovalsApproved,
                        "Restoring original order saved or authorized trial removal.");
                    continue;
                }
                var expectedRemoval = !confirmRemoval || approveRemoval;
                Assert(stoppedTrialSaves == 1 && Order().SequenceEqual(expectedRemoval ? testedTrial : keptTrial)
                    && stoppedResult.Maintenance.TrialDecisionRecorded
                    && savedStopped.TrialDecisionRecorded && savedStopped.TrialRemovalsApproved == expectedRemoval
                    && stoppedResult.Maintenance.TrialRemovalsApproved == expectedRemoval && !stoppedResult.CandidateApplied,
                    "Stopped save confused keeping order with approving removals, or claimed success.");
                if (outcome == MaintenanceOutcome.Cancelled && confirmRemoval && !approveRemoval && !insertion)
                {
                    window.ShowAutomaticResult(stoppedResult);
                    Assert(window.SummaryText.Text.Contains("尚未验证通过") && !window.SummaryText.Text.Contains("通过验证的是"),
                        "Keeping mods after cancellation displayed a passing trial that never completed.");
                }
                vm.UndoCommand.Execute(null);
                Assert(Order().SequenceEqual(stoppedRequest.CurrentOrder), "Stopped trial decision cannot be undone.");
            }
            checks.Add("stopped/cancelled/failed full and insertion tasks separately honor removal consent, default to keeping mods, preserve adjustments/dependencies and undo");
            foreach (var invalidCheckpoint in new[] { true, false })
            {
                ResetTrial();
                var stoppedRequest = AiSortingSession.CreateRequest(vm, settings, null);
                string stoppedTaskId;
                using (var task = session.Tasks.Create(new(AiTaskKind.Maintenance,
                    invalidCheckpoint ? AiTaskPhase.Testing : AiTaskPhase.Cancelled,
                    AutomaticGameMaintenance.RecoveryContext(stoppedRequest, vm.CurrentCatalogue!, [], GameVersion.Parse("1.7")!),
                    stoppedRequest.CurrentOrder.ToArray(), testedTrial, DateTimeOffset.UtcNow)
                    { Limits = limits, DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(15), Attempts = 2,
                        TrialExclusions = [reorderedTrial] })) stoppedTaskId = task.Id;
                var stoppedTrial = new AutomaticMaintenanceResult(new(MaintenanceOutcome.Cancelled,
                    "合成取消", 2, 0, 0, false, testedTrial, true)
                    { TaskId = stoppedTaskId, TrialExclusions = [reorderedTrial] }, null, []);
                var failedSaves = 0;
                automatic.RememberStoppedOrder(stoppedTrial, stoppedRequest, settings, () => paths, paths, vm.ContentRevision,
                    vm.CurrentCatalogue!, () => { failedSaves++; throw new IOException("Synthetic stopped save failure"); });
                if (invalidCheckpoint)
                {
                    MustRejectCheckpoint(() => automatic.ResolveLastOrder(stoppedTrial, true, true));
                    Assert(failedSaves == 0 && Order().SequenceEqual(stoppedRequest.CurrentOrder),
                        "Checkpoint preflight happened after editor/game writes.");
                }
                else
                {
                    try { automatic.ResolveLastOrder(stoppedTrial, true, true); throw new Exception("Save failure hidden"); }
                    catch (IOException ex) when (ex.Message == "Synthetic stopped save failure") { }
                    Assert(failedSaves == 1, "Game save was not attempted exactly once.");
                    vm.UndoCommand.Execute(null);
                }
                using var reopened = session.Tasks.Open(stoppedTaskId);
                Assert(reopened.Current.StoppedOrderDecision is null && !reopened.Current.TrialDecisionRecorded,
                    "A failed save was recorded as a completed stopped-order choice.");
            }
            checks.Add("persisted stopped decisions survive reopening across 112 choices; preflight prevents invalid-state edits and failed saves remain uncommitted");
            foreach (var approve in new[] { false, true })
            {
                ResetTrial();
                request = AiSortingSession.CreateRequest(vm, settings, null) with { OutputTokenLimit = limits.OutputTokensPerRequest };
                string trialTaskId;
                using (var task = session.Tasks.Create(new(AiTaskKind.Maintenance, AiTaskPhase.Completed,
                    AutomaticGameMaintenance.RecoveryContext(request, vm.CurrentCatalogue!, [], GameVersion.Parse("1.7")!),
                    request.CurrentOrder.ToArray(), testedTrial, DateTimeOffset.UtcNow)
                    { Limits = limits, DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(15), CandidatePendingApply = true,
                        Attempts = 2, TrialExclusions = [reorderedTrial], UnknownUsage = true, ReservedTokens = 500 })) trialTaskId = task.Id;
                var confirmations = 0; saves = 0; callsBeforePublish = client.Calls;
                var completedTrial = await automatic.RunAsync(() => paths, settings, limits, new Progress<MaintenanceProgress>(),
                    new Progress<OrdinaryGameProgress>(), new Progress<AiResponseUpdate>(), default, trialTaskId,
                    prepareSave: () => () => { Assert(Order().SequenceEqual(approve ? testedTrial : keptTrial), "Save saw the wrong trial order."); saves++; }, confirmTrials: (_, _) =>
                    { confirmations++; return Task.FromResult(approve); });
                var checkpoint = session.Tasks.Inspect().Single(t => t.Id == trialTaskId).Checkpoint!;
                Assert(confirmations == 1 && saves == 1 && client.Calls == callsBeforePublish
                    && completedTrial.Maintenance.TrialRemovalsApproved == approve
                    && checkpoint.TrialRemovalsApproved == approve && checkpoint.TrialDecisionRecorded
                    && !checkpoint.CandidatePendingApply && checkpoint.UnknownUsage && checkpoint.ReservedTokens == 500
                    && completedTrial.Maintenance.HasUnknownUsage,
                    "Trial recovery lost the final decision or issued a paid request.");
                Assert(Order().SequenceEqual(approve ? testedTrial : keptTrial), "Trial choice lost sorted rows or retained mods.");
                vm.UndoCommand.Execute(null);
                Assert(Order().SequenceEqual(request.CurrentOrder), "Trial decision was not one undoable edit.");
            }
            checks.Add("pending trial recovery asks once and saves removals or retained mods with pre/post trial sorting and prerequisites without AI or game calls");

            var warningSuccess = new AutomaticMaintenanceResult(new(MaintenanceOutcome.Passed,
                "已排序完成：已自动保存", 1, 0, 0, false, testedTrial, true) { HasWarnings = true }, null, [])
                { CandidateApplied = true };
            Assert(AiSortingWindow.CompletionNotice(warningSuccess)?.Contains("仅有黄字") == true,
                "Warning-only success must produce the completion dialog.");
            Assert(AiSortingWindow.CompletionNotice(warningSuccess with { Maintenance = warningSuccess.Maintenance with
                { TrialExclusions = [reorderedTrial] } }) is null,
                "Trial result must use the removal choice instead of a second success dialog.");
            Assert(AiSortingWindow.CompletionNotice(warningSuccess with { CandidateApplied = false }) is null
                && AiSortingWindow.CompletionNotice(warningSuccess with { Maintenance = warningSuccess.Maintenance with
                    { Outcome = MaintenanceOutcome.Stopped } }) is null, "Unapplied or failed results cannot show success.");
            checks.Add("warning-only completion dialog and trial-choice routing never report unsaved or failed results as success");

            foreach (var approve in new[] { false, true })
            {
            ResetTrial();
            request = AiSortingSession.CreateRequest(vm, settings, null) with { OutputTokenLimit = limits.OutputTokensPerRequest };
            using (var task = session.Tasks.Create(new(AiTaskKind.Maintenance, AiTaskPhase.Completed,
                AutomaticGameMaintenance.RecoveryContext(request, vm.CurrentCatalogue!, [], GameVersion.Parse("1.7")!),
                request.CurrentOrder.ToArray(), testedTrial, DateTimeOffset.UtcNow)
                { Limits = limits, DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(15), CandidatePendingApply = true,
                    Attempts = 2, TrialExclusions = [reorderedTrial] })) pendingId = task.Id;
            var trialConfirmations = 0; callsBeforePublish = client.Calls;
            try
            {
                await automatic.RunAsync(() => paths, settings, limits, new Progress<MaintenanceProgress>(),
                    new Progress<OrdinaryGameProgress>(), new Progress<AiResponseUpdate>(), default, pendingId,
                    prepareSave: () => () => throw new IOException("合成试排除保存失败"),
                    confirmTrials: (_, _) => { trialConfirmations++; return Task.FromResult(approve); });
                throw new InvalidOperationException("Trial save failure was hidden.");
            }
            catch (IOException ex) when (ex.Message == "合成试排除保存失败") { }
            var pendingDecision = session.Tasks.Inspect().Single(t => t.Id == pendingId).Checkpoint!;
            Assert(Order().SequenceEqual(approve ? testedTrial : keptTrial)
                && pendingDecision.CandidatePendingApply && pendingDecision.TrialDecisionRecorded
                && pendingDecision.TrialRemovalsApproved == approve, "Save failure lost trial decision.");
            appliedRevision = vm.ContentRevision; saves = 0;
            await automatic.RunAsync(() => paths, settings, limits, new Progress<MaintenanceProgress>(),
                new Progress<OrdinaryGameProgress>(), new Progress<AiResponseUpdate>(), default, pendingId,
                prepareSave: () => () => saves++, confirmTrials: (_, _) =>
                { trialConfirmations++; return Task.FromResult(!approve); });
            Assert(saves == 1 && trialConfirmations == 1 && vm.ContentRevision == appliedRevision && client.Calls == callsBeforePublish
                && !session.Tasks.Inspect().Single(t => t.Id == pendingId).Checkpoint!.CandidatePendingApply,
                "Decided save recovery reasked, edited or billed again.");
            vm.UndoCommand.Execute(null);
            Assert(Order().SequenceEqual(request.CurrentOrder), "Resumed save changed undo history.");
            }
            checks.Add("either trial decision survives failed save and resumes without repeat confirmation editing or requests");

            Reset();
            var offline = new DatabaseStore(root).Load().Snapshot;
            request = AiSortingSession.CreateRequest(vm, settings, null) with { DatabaseSnapshotId = offline.Id };
            revision = vm.ContentRevision;
            var beforeCatalogue = vm.CurrentCatalogue;
            var pinRejected = false;
            try
            {
                automatic.PublishCandidate(candidate with { Maintenance = candidate.Maintenance with { TaskId = null } },
                    request, settings, () => paths, paths, revision, beforeCatalogue!, default, database: offline);
            }
            catch (InvalidDataException) { pinRejected = true; }
            Assert(pinRejected && ReferenceEquals(vm.CurrentCatalogue, beforeCatalogue), "Mismatched database result published its scan.");
            Unchanged();
            vm.SetCommunityDatabase(offline);
            automatic.PublishCandidate(candidate with { DatabaseSnapshotId = offline.Id }, request, settings,
                () => paths, paths, revision, beforeCatalogue!, default, database: offline);
            Unchanged();
            checks.Add("database pin mismatch refuses metadata publication and database changes invalidate in-flight editor results");

            Reset();
            string missingPinTask;
            using (var task = session.Tasks.Create(new(AiTaskKind.Maintenance, AiTaskPhase.Paused, new string('a',64),
                request.CurrentOrder.ToArray(), request.CurrentOrder.ToArray(), DateTimeOffset.UtcNow)
                { DatabaseSnapshotId = new string('c',64), Limits = limits, DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(15) }))
                missingPinTask = task.Id;
            callsBeforePublish = client.Calls;
            var missingPinRejected = false;
            try
            {
                await automatic.RunAsync(() => paths, settings, limits, new Progress<MaintenanceProgress>(),
                    new Progress<OrdinaryGameProgress>(), new Progress<AiResponseUpdate>(), default, missingPinTask);
            }
            catch (FileNotFoundException) { missingPinRejected = true; }
            Assert(missingPinRejected && client.Calls == callsBeforePublish, "Missing task snapshot was substituted or caused a request.");
            checks.Add("missing task database pin stops recovery before AI or game execution");

            await CheckDatabaseWindowAsync(main, root, output, checks);

            LoadOrderRow DisplayRow(string id, string name, int index)
            {
                var row = new LoadOrderRow(id, index);
                var installation = Mod(id) with
                {
                    Source = index % 2 == 0 ? ModSource.Local : ModSource.Workshop,
                    Metadata = Mod(id).Metadata! with { Name = name },
                    CodeKind = index % 2 == 0 ? ModCodeKind.Xml : ModCodeKind.CSharp
                };
                row.SetMatch(new OrderMatch(id, [installation]));
                if (index == 1) row.SetColor(MarkerPalette.Colors[0].Hex);
                if (index == 2) row.SetDiagnostics([new("SyntheticWarning", DiagnosticSeverity.Warning, "合成警告原因")], true);
                row.SetActivity(false, index == 3, updatedUtc: DateTimeOffset.UtcNow);
                return row;
            }
            var trialRows = Enumerable.Range(1, 100).Select(i => new TrialRemovalRow(
                DisplayRow("synthetic.trial." + i, i == 1 ? "合成模组：长名称与颜色标记显示" : "合成模组 " + i, i),
                i == 1 ? "试排除项" : "连带依赖项")).ToArray();
            var trialWindow = new TrialExclusionWindow(trialRows, true) { Owner = main };
            trialWindow.Show();
            try
            {
                trialWindow.Width = trialWindow.MinWidth; trialWindow.Height = trialWindow.MinHeight;
                foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
                {
                    ((App)Application.Current).Themes.Select(theme, save: false);
                    await trialWindow.Dispatcher.InvokeAsync(trialWindow.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    foreach (var button in new[] { trialWindow.KeepButton, trialWindow.ConfirmButton })
                    {
                        var bounds = new Rect(button.TranslatePoint(new Point(), trialWindow), button.RenderSize);
                        Assert(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= trialWindow.ActualWidth
                            && bounds.Bottom <= trialWindow.ActualHeight, "Trial confirmation action clipped at minimum size.");
                    }
                    Assert(trialWindow.StatusText.Text.Contains("黄字") && trialWindow.StatusText.Text.Contains("不能证明"),
                        "Trial result hides warnings or claims proven culprit.");
                    Assert(trialWindow.ModsList.Items.Count == 100 && Equals(trialWindow.ConfirmButton.Content, "移除模组")
                        && Equals(trialWindow.KeepButton.Content, "不做处理"), "Trial dialog has no direct removal actions or mod rows.");
                    var firstRow = (DataGridRow)trialWindow.ModsList.ItemContainerGenerator.ContainerFromIndex(0);
                    var expected = (System.Windows.Media.SolidColorBrush)new MarkerBackgroundConverter().Convert(
                        [trialRows[0].MarkerColor!, 0, trialWindow.ModsList.Background], typeof(System.Windows.Media.Brush), "", System.Globalization.CultureInfo.InvariantCulture);
                    Assert(((System.Windows.Media.SolidColorBrush)firstRow.Background).Color == expected.Color,
                        "Trial row lost the original color marker.");
                    PrototypeSmokeCheck.SavePreview(trialWindow, Path.Combine(output, theme + "-试排除确认最小尺寸.png"));
                }
            }
            finally { trialWindow.Close(); }
            checks.Add("trial confirmation fits minimum size in both themes and distinguishes warning status from proof");

            var conflict = new DeclaredConflict("first.mod", "second.mod", "合成冲突 " + new string('长', 100),
                [new("first.mod", new string('名', 80), ["first.mod", "child.mod"], null),
                 new("second.mod", "必须保留", ["second.mod"], "保护颜色必须保留") { RetentionOverrideIds = ["second.mod"] }]);
            LoadOrderRow ResolveChoice(string id)
            {
                var option = conflict.Options.Single(o => o.Identifier == id);
                return DisplayRow(id, option.Name, Array.IndexOf(conflict.Options.ToArray(), option) + 1);
            }
            var conflictWindow = new ConflictChoiceWindow(conflict, ResolveChoice) { Owner = main };
            conflictWindow.Show();
            try
            {
                conflictWindow.Width = conflictWindow.MinWidth; conflictWindow.Height = conflictWindow.MinHeight;
                foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
                {
                    ((App)Application.Current).Themes.Select(theme, save: false);
                    await conflictWindow.Dispatcher.InvokeAsync(conflictWindow.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    Assert(conflictWindow.SelectedIds is null && !conflictWindow.ContinueButton.IsEnabled
                        && conflictWindow.ModsList.SelectedItems.Count == 0 && conflictWindow.Rows[1].IsProtected,
                        "Conflict dialog preselected a removal or allowed protected content.");
                    var button = conflictWindow.ContinueButton;
                    var bounds = new Rect(button.TranslatePoint(new Point(), conflictWindow), button.RenderSize);
                    Assert(bounds.Right <= conflictWindow.ActualWidth && bounds.Bottom <= conflictWindow.ActualHeight,
                        "Conflict action is clipped.");
                    PrototypeSmokeCheck.SavePreview(conflictWindow, Path.Combine(output, theme + "-冲突选择最小尺寸.png"));
                }
            }
            finally { conflictWindow.Close(); }
            checks.Add("conflict dialog has no default removal, protects retained mods and fits minimum size in both themes");

            conflict = new("first.mod", "second.mod", "合成组合待排查：三个同时启用才出现当轮报错。",
                [new("first.mod", "模组甲", ["first.mod", "child.mod"], null),
                 new("second.mod", "模组乙", ["second.mod"], "保护颜色必须保留") { RetentionOverrideIds = ["second.mod"] },
                 new("third.mod", "模组丙", ["third.mod"], null)])
            { RuntimeEvidence = new(["first.mod", "second.mod", "third.mod"],
                ["first.mod", "second.mod", "third.mod", "child.mod"], ["GamePatchFailure"]) };
            conflictWindow = new(conflict, ResolveChoice) { Owner = main };
            conflictWindow.Show();
            try
            {
                var choices = conflictWindow.Rows;
                Assert(conflictWindow.HeadingText.Text.Contains("3 个") && conflictWindow.GroupStatusText.Text.Contains("整体组合"),
                    "Three-way conflict was presented as pairwise proof.");
                foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
                {
                    ((App)Application.Current).Themes.Select(theme, save: false);
                    var pendingColor = ((System.Windows.Media.SolidColorBrush)conflictWindow.GroupStatusBorder.Background).Color;
                    conflictWindow.ModsList.SelectedItems.Add(choices[0]);
                    Assert(conflictWindow.SelectedIds!.SequenceEqual(["first.mod"])
                        && conflictWindow.ContinueButton.IsEnabled && conflictWindow.GroupStatusText.Text.Contains("尝试继续测试")
                        && conflictWindow.ImpactText.Text.Contains("child.mod") && conflictWindow.ImpactText.Text.Contains("不代表复测已通过"),
                        "Selection did not preview dependent removals and conditional retest.");
                    var readyColor = ((System.Windows.Media.SolidColorBrush)conflictWindow.GroupStatusBorder.Background).Color;
                    Assert(pendingColor != readyColor, "Selection did not change the pending red background to green.");
                    conflictWindow.ModsList.UpdateLayout();
                    var protectedRow = (DataGridRow)conflictWindow.ModsList.ItemContainerGenerator.ContainerFromItem(choices[1]);
                    Assert(conflictWindow.ProtectionWarning.Visibility == Visibility.Visible
                        && conflictWindow.ProtectionText.Text.Contains("模组乙")
                        && ((System.Windows.Media.SolidColorBrush)protectedRow.Background).Color
                            == ((System.Windows.Media.SolidColorBrush)conflictWindow.FindResource("ConflictProtectedBrush")).Color
                        && choices[1].State.Contains("必须保留"),
                        $"Protected warning mismatch: visible={conflictWindow.ProtectionWarning.Visibility}, text={conflictWindow.ProtectionText.Text}, "
                        + $"state={choices[1].State}, background={protectedRow.Background}, expected={conflictWindow.FindResource("ConflictProtectedBrush")}.");
                    conflictWindow.ModsList.SelectedItems.Add(choices[2]);
                    Assert(conflictWindow.SelectedIds!.SequenceEqual(["first.mod", "third.mod"])
                        && choices[1].IsProtected, "Multiple selection or protected member failed.");
                    conflictWindow.ModsList.SelectedItems.Add(choices[1]);
                    Assert(conflictWindow.SelectedIds!.Contains("second.mod") && conflictWindow.ContinueButton.IsEnabled
                        && conflictWindow.GroupStatusText.Text.Contains("覆盖") && choices[1].State.Contains("覆盖保留"),
                        "Explicit player selection could not override retention with a warning.");
                    conflictWindow.ModsList.UpdateLayout();
                    var protectedCell = (DataGridCell)((DataGridCellsPresenter)FindChild(protectedRow, typeof(DataGridCellsPresenter))!)
                        .ItemContainerGenerator.ContainerFromIndex(1);
                    Assert(((System.Windows.Media.SolidColorBrush)protectedCell.Background).Color
                        == ((System.Windows.Media.SolidColorBrush)protectedRow.Background).Color,
                        "Selecting a protected row obscured its warning color.");
                    conflictWindow.ModsList.SelectedItems.Remove(choices[1]);
                    conflictWindow.Width = conflictWindow.MinWidth; conflictWindow.Height = conflictWindow.MinHeight;
                    await conflictWindow.Dispatcher.InvokeAsync(conflictWindow.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    PrototypeSmokeCheck.SavePreview(conflictWindow, Path.Combine(output, theme + "-组合冲突可复测.png"));
                    conflictWindow.ModsList.SelectedItems.Clear();
                    Assert(!conflictWindow.ContinueButton.IsEnabled && conflictWindow.SelectedIds is null
                        && ((System.Windows.Media.SolidColorBrush)conflictWindow.GroupStatusBorder.Background).Color == pendingColor,
                        "Undoing selections left the conflict green or executable.");
                }
                conflictWindow.ModsList.SelectedItems.Add(choices[2]);
                var darkReady = ((System.Windows.Media.SolidColorBrush)conflictWindow.GroupStatusBorder.Background).Color;
                ((App)Application.Current).Themes.Select(ThemePreference.Light, save: false);
                Assert(conflictWindow.SelectedIds!.SequenceEqual(["third.mod"])
                    && ((System.Windows.Media.SolidColorBrush)conflictWindow.GroupStatusBorder.Background).Color != darkReady,
                    "Theme hot switching lost the selection or kept the old conflict background.");
                conflictWindow.Width = 760; conflictWindow.Height = 1000;
                await conflictWindow.Dispatcher.InvokeAsync(conflictWindow.UpdateLayout, DispatcherPriority.ApplicationIdle);
                PrototypeSmokeCheck.SavePreview(conflictWindow, Path.Combine(output, "Light-保护模组完整提醒.png"));
            }
            finally { conflictWindow.Close(); }
            checks.Add("whole three-way conflict, multi-select, dependent preview, red/green reversible status and theme hot switching");

            bool? ClickDialog(Window dialog, Button button)
            {
                dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                    new Action(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                return dialog.ShowDialog();
            }
            foreach (var remove in new[] { false, true })
            {
                var trialAction = new TrialExclusionWindow(trialRows, false) { Owner = main };
                Assert(ClickDialog(trialAction, remove ? trialAction.ConfirmButton : trialAction.KeepButton) == remove,
                    "Trial direct action returned the wrong decision.");
                var conflictAction = new ConflictChoiceWindow(conflict, ResolveChoice) { Owner = main };
                conflictAction.ModsList.SelectedItems.Add(conflictAction.Rows[0]);
                Assert(ClickDialog(conflictAction, remove ? conflictAction.ContinueButton : conflictAction.KeepButton) == remove,
                    "Conflict direct action returned the wrong decision.");
                Assert(conflictAction.SelectedIds!.SequenceEqual(["first.mod"]), "Direct action changed the removal selection.");
            }
            checks.Add("direct remove and keep buttons return explicit decisions in both modal dialogs");

            foreach (var warnings in new[] { false, true })
            {
                var successDialog = new TrialExclusionWindow([], warnings) { Owner = main };
                Assert(successDialog.Title == "自动排序结果" && successDialog.HeadingText.Text == "排序完成"
                    && successDialog.ModsList.Visibility == Visibility.Collapsed
                    && successDialog.KeepButton.Visibility == Visibility.Collapsed
                    && Equals(successDialog.ConfirmButton.Content, "确定")
                    && successDialog.ChoiceText.Text.Contains("排序已保存"),
                    "Unified success dialog must show saved completion without removal controls.");
                Assert(ClickDialog(successDialog, successDialog.ConfirmButton) == true,
                    "Unified completion dialog did not close on confirmation.");
            }
            checks.Add("unified result window shows either saved success or tested mod choices; unknown billing preserves successful publication");

            Reset();
            Assert(window.WaitNoticeDelay == TimeSpan.FromSeconds(30), "Sorting reminder default is not 30 seconds.");
            window.WaitNoticeDelay = TimeSpan.FromMilliseconds(50);
            var streamed = new TaskCompletionSource<AiClientResult>();
            AiRequest? sent = null;
            CancellationToken sentToken = default;
            client.Response = (request, token) => { sent = request; sentToken = token; return streamed.Task; };
            var streaming = window.StartSortingAsync();
            // Rendering both themes can delay a background DispatcherTimer. Wait for its observable
            // result rather than assuming it ran within a single 250 ms scheduling window.
            var reminderDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!streaming.IsCompleted && !window.StatusText.Text.Contains("可能超时")
                && DateTimeOffset.UtcNow < reminderDeadline) await Task.Delay(25);
            Assert(!streaming.IsCompleted && !sentToken.IsCancellationRequested && window.CancelButton.IsEnabled,
                "Sorting reminder cancelled or completed the pending request.");
            Assert(window.StatusText.Text.Contains("可能超时"), "Sorting reminder was not displayed.");
            sent!.ResponseProgress!.Report(new("真实合成回报片段"));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(window.ResponseText.Text.Contains("真实合成回报片段") && !window.ApplyButton.IsEnabled,
                "Live content was not displayed or partial content enabled application.");
            streamed.SetResult(GoodReply() with { PublicContent = "最终合成回报" });
            await streaming;
            Assert(window.ResponseText.Text == "最终合成回报" && window.ApplyButton.IsEnabled,
                "Completed streamed result lost its content or failed validation.");
            sent.ResponseProgress.Report(new("迟到片段"));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(window.ResponseText.Text == "最终合成回报", "Late progress overwrote final content.");
            window.WaitNoticeDelay = TimeSpan.FromSeconds(30);
            checks.Add("30-second soft reminder, real response progress, no partial apply, delayed success and stale progress isolation");

            Reset();
            var pending = new TaskCompletionSource<AiClientResult>();
            client.Response = (_, _) => pending.Task;
            var active = window.StartSortingAsync();
            Assert(!window.PermissionBox.IsEnabled, "Running request allowed a permission mode change.");
            var count = client.Calls;
            await window.StartSortingAsync();
            Assert(client.Calls == count && !window.AutomaticButton.IsEnabled, "Duplicate request was sent.");
            vm.ClearOrderCommand.Execute(null);
            pending.SetResult(GoodReply()); await active;
            Assert(window.PermissionBox.IsEnabled, "Finished request left permission selection disabled.");
            Assert(!window.ApplyButton.IsEnabled, "Mid-request edit accepted stale result.");
            checks.Add("duplicate prevention and mid-request edits");

            Reset(); pending = new(); client.Response = (_, _) => pending.Task;
            active = window.StartSortingAsync();
            store.Save(settings with { Model = "changed-mid-request" });
            pending.SetResult(GoodReply()); await active; Unchanged();
            Assert(!window.ApplyButton.IsEnabled && window.StatusText.Text.Contains("设置已变化"), "Mid-request settings change was not invalidated.");
            checks.Add("mid-request settings change invalidates response without extra request");

            Reset(); pending = new(); client.Response = (_, _) => pending.Task;
            active = window.StartSortingAsync();
            window.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pending.SetResult(GoodReply()); await active; Unchanged();
            Assert(!window.ApplyButton.IsEnabled && window.StatusText.Text.Contains("取消") && window.TokenText.Text.Contains("150"),
                "Late cancellation result became applicable or lost returned usage.");
            checks.Add("late response after cancellation cannot apply and preserves billing evidence");

            Reset(); client.Response = (_, token) => Task.FromCanceled<AiClientResult>(new CancellationToken(true));
            await window.StartSortingAsync();
            Assert(window.TokenText.Text.Contains("未知") && !window.ApplyButton.IsEnabled, "Cancellation invented actual usage.");
            checks.Add("cancelled request without response reports unknown actual usage");

            var tasks = new AiTaskRecoveryStore(root);
            var unknownTask = tasks.Inspect().Single(t => t.Checkpoint is { Kind: AiTaskKind.Sorting, UnknownUsage: true });
            count = client.Calls;
            client.Response = (_, _) => Task.FromResult(GoodReply());
            await window.StartSortingAsync();
            Assert(client.Calls == count + 1 && tasks.Inspect().Single(t => t.Id == unknownTask.Id).Checkpoint!.UnknownUsage,
                "New request must preserve old unknown billing without replaying the old task.");
            tasks.Discard(unknownTask.Id);
            await window.StartSortingAsync();
            Assert(client.Calls == count + 2 && tasks.InspectDeletedUsage().Single(t => t.Id == unknownTask.Id).UnknownUsage,
                "Discard must retain billing evidence without blocking new requests.");
            // Synthetic fixture evidence only; production reconciliation requires the player's provider record.
            tasks.ReconcileUsage(unknownTask.Id, new(0, 0, 0));
            checks.Add("unknown billing remains recorded before and after discard without blocking new requests");

            Reset(); client.Response = (_, _) => Task.FromResult(GoodReply());
            var firstSession = new AiSortingSession(vm, root, client, secrets);
            var saved = await firstSession.RequestAsync(settings, firstSession.Estimate(settings), default);
            count = client.Calls; var secretReads = secrets.Reads;
            var restoredSession = new AiSortingSession(vm, root, client, secrets);
            var restored = restoredSession.Recover(saved.TaskId!, settings);
            Assert(client.Calls == count && secrets.Reads == secretReads, "Recovery sent a request or read a key.");
            restoredSession.Apply(restored);
            MustReject(() => restoredSession.Apply(restored));
            Assert(Order()[0] == "second.mod", "Recovered result was not applied.");
            MustReject(() => restoredSession.Recover(saved.TaskId!, settings));
            Assert(tasks.Inspect().Single(t => t.Id == saved.TaskId).Checkpoint!.Phase == AiTaskPhase.Completed,
                "Already applied result was not reconciled idempotently.");
            checks.Add("new AI session restores paid result without network or key and never applies twice");

            Reset(); saved = await firstSession.RequestAsync(settings, firstSession.Estimate(settings), default);
            using (var interrupted = tasks.Open(saved.TaskId!))
                interrupted.Save(interrupted.Current with { Phase = AiTaskPhase.Applying,
                    CurrentOrder = saved.Plan!.After.ToArray() });
            // Editor snapshot still had the original order at the moment of interruption.
            restored = restoredSession.Recover(saved.TaskId!, settings);
            restoredSession.Apply(restored);
            Assert(Order()[0] == "second.mod", "Interrupted application could not recover from original snapshot.");
            checks.Add("application interrupted before editor snapshot can recover its validated candidate");

            Reset(); saved = await firstSession.RequestAsync(settings, firstSession.Estimate(settings), default);
            vm.ClearOrderCommand.Execute(null);
            MustReject(() => restoredSession.Recover(saved.TaskId!, settings));
            Assert(vm.Rows.Count == 0, "Recovery overwrote later manual edits.");
            checks.Add("restoration refuses changed editor context without overwriting manual edits");

            Reset();
            var recoveryWindow = new TaskRecoveryWindow(tasks) { Owner = main };
            recoveryWindow.Show();
            try
            {
                recoveryWindow.Width = recoveryWindow.MinWidth; recoveryWindow.Height = recoveryWindow.MinHeight;
                foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
                {
                    ((App)Application.Current).Themes.Select(theme, save: false);
                    await recoveryWindow.Dispatcher.InvokeAsync(recoveryWindow.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var panel = (DockPanel)recoveryWindow.Content;
                    var recoveryList = panel.Children.OfType<ListBox>().Single();
                    var recoveryButtons = panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
                    Assert(recoveryList.Items.Count > 0 && recoveryList.ActualHeight >= 36, "Recovery list collapsed.");
                    foreach (var button in recoveryButtons)
                    {
                        var bounds = new Rect(button.TranslatePoint(new Point(), panel), button.RenderSize);
                        Assert(button.ActualWidth > 0 && bounds.Left >= 0 && bounds.Right <= panel.ActualWidth + 1
                            && bounds.Bottom <= panel.ActualHeight + 1, "Recovery button is clipped.");
                    }
                    PrototypeSmokeCheck.SavePreview(recoveryWindow, Path.Combine(output, theme + "-恢复任务最小尺寸.png"));
                }
                var finished = tasks.Inspect().Where(t => t.Checkpoint is { Finished: true, UnknownUsage: false }).Select(t => t.Id).ToArray();
                Assert(finished.Length > 0, "Cleanup fixture has no finished history.");
                var recoveryPanel = (DockPanel)recoveryWindow.Content;
                var clean = recoveryPanel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>()
                    .Single(b => Equals(b.Content, "清理已结束任务"));
                var beforeCalls = client.Calls;
                clean.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (!clean.IsEnabled && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
                Assert(clean.IsEnabled && client.Calls == beforeCalls, "Cleanup did not finish locally.");
                var deletedUsage = tasks.InspectDeletedUsage().Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
                Assert(tasks.Inspect().All(t => !finished.Contains(t.Id))
                    && finished.All(deletedUsage.Contains), "Cleanup lost billing or retained finished payloads.");
                Assert(recoveryPanel.Children.OfType<TextBlock>().Any(t => t.Text.StartsWith("已清理", StringComparison.Ordinal)),
                    "Refreshing the list erased the cleanup result.");
                checks.Add("finished history cleanup runs locally and retains billing and its visible result");
            }
            finally { recoveryWindow.Close(); }
            checks.Add("recovery task window preserves records and fits minimum size in day and night themes");

            Reset(); client.Response = (_, _) => Task.FromResult(GoodReply());
            await window.StartSortingAsync();
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            Assert(window.DependencyDetails.Visibility == Visibility.Collapsed, "Empty prerequisite details were visible");
            window.ShowDependencyReasons(["共享前置 [base.mod]：由 中文模组甲 [owner.a]、中文模组乙 [owner.b] 要求；依据：About.xml 必要前置（游戏 1.7）。",
                "下一层前置 [nested.mod]：由 共享前置 [base.mod] 要求；依据：About.xml 必要前置（游戏 1.7）。"]);
            window.StatusText.Text = "正在等待 Steam 下载…";
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert(window.AutomaticButton.IsVisible && window.FindName("StartButton") is null && window.ApplyButton.ActualWidth > 0 && window.AcceptedList.ActualHeight >= 36,
                    "Minimum-size layout collapsed.");
                Assert(window.DependencyDetails.IsVisible && window.DependencyList.Items.Count == 2
                    && window.DependencyList.ActualHeight > 20 && window.DependencyList.ActualWidth > 100,
                    "Prerequisite reasons were hidden or erased by download status");
                var reasonTop = window.DependencyList.TranslatePoint(new Point(), window.StatusScroll).Y;
                Assert(reasonTop >= 0 && reasonTop + 40 < window.StatusScroll.ViewportHeight,
                    $"Prerequisite reasons fell outside the visible status viewport: top={reasonTop}, viewport={window.StatusScroll.ViewportHeight}, height={window.StatusScroll.ActualHeight}, offset={window.StatusScroll.VerticalOffset}");
                var buttons = new[] { window.AutomaticButton, window.CancelButton, window.ApplyButton, window.CloseButton };
                foreach (var button in buttons)
                {
                    var bounds = new Rect(button.TranslatePoint(new Point(), window), button.RenderSize);
                    Assert(bounds.Left >= 0 && bounds.Right <= window.ActualWidth
                        && bounds.Bottom <= window.ActualHeight
                        && button.ActualWidth + button.Margin.Left + button.Margin.Right + 0.5 >= button.DesiredSize.Width,
                        $"Bottom button {button.Name} was clipped at minimum window size: {bounds}, desired {button.DesiredSize}.");
                }
                for (var i = 0; i < buttons.Length; i++)
                    for (var j = i + 1; j < buttons.Length; j++)
                    {
                        Rect Bounds(Button b) => new(b.TranslatePoint(new Point(), window), b.RenderSize);
                        Assert(!Bounds(buttons[i]).IntersectsWith(Bounds(buttons[j])), "Bottom buttons overlap.");
                    }
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, theme + "-AI排序最小尺寸.png"));
            }
            checks.Add("day/night hot switch and minimum-size non-overlapping controls");
            window.ShowDependencyReasons(null);
            Assert(window.DependencyDetails.Visibility == Visibility.Collapsed && window.DependencyList.Items.Count == 0,
                "New task did not clear previous prerequisite details");
            checks.Add("prerequisite owners readable in both themes at minimum size, persist across status updates, clear for new tasks");

            var startupDetails = new StartupFailureDetails(new("Exited", true, true, [new("GraphicsDriverUnavailable")]),
                [Path.Combine(root, "本轮 Player.log"), Path.Combine(root, "launch.json")],
                new("environment", "合成显卡初始化失败；自动排序已停止。", ["请携带本轮日志向专业人员求助。"]));
            window.ShowStartupFailure(startupDetails);
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                window.StartupFailureText.SelectAll();
                Assert(window.StartupFailureText.IsVisible && window.StartupFailureText.IsReadOnly
                    && window.StartupFailureText.SelectedText.Contains(startupDetails.LogPaths[0])
                    && window.StartupFailureText.Text.Contains("不要只带着此界面的截图")
                    && window.StartupFailureText.ActualWidth > 100 && window.StartupFailureText.ActualHeight >= 120
                    && window.ResultPanel.Visibility == Visibility.Visible, "Startup advice or selectable log paths missing.");
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, theme + "-启动排障.png"));
            }
            window.ShowStartupFailure(startupDetails with { Diagnosis = null }, AiReplyFailure.InvalidStructure);
            Assert(window.StartupFailureText.Text.Contains("AI 回复结构未通过校验")
                && window.StartupFailureText.Text.Contains(startupDetails.LogPaths[0]),
                "Rejected reply reason or support logs missing from failure details.");
            window.ShowStartupFailure(null);
            Assert(window.StartupFailureText.Visibility == Visibility.Collapsed && window.StartupFailureText.Text.Length == 0
                && window.ResultPanel.Visibility == Visibility.Visible,
                "Old startup advice remained in a new task.");
            checks.Add("startup advice and selectable Unicode log paths, help reminder, both themes and stale result clearing");

            var monitorPending = new TaskCompletionSource<AiProviderUsageResult>();
            var monitorCalls = 0;
            var monitor = new AiUsageMonitorWindow(store, secrets, tasks) { Owner = main,
                QueryForCheck = async (_, _, token) => { monitorCalls++; return await monitorPending.Task.WaitAsync(token); } };
            monitor.Show();
            try
            {
                var monitorDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while (monitorCalls == 0 && DateTimeOffset.UtcNow < monitorDeadline) await Task.Delay(10);
                Assert(monitorCalls == 1 && monitor.BusyForCheck, "Usage monitor did not begin asynchronous polling.");
                // The dispatcher must remain usable while the HTTP response is outstanding.
                await monitor.Dispatcher.InvokeAsync(() => monitor.Title = "AI 用量与余额");
                monitorPending.SetResult(new(DateTimeOffset.UtcNow, new(1750000, 1250000, 500000, true, 500000, 1, "CNY"),
                    null, new(100, 20, 1000, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow), null));
                while (monitor.BusyForCheck && DateTimeOffset.UtcNow < monitorDeadline) await Task.Delay(10);
                Assert(!monitor.BusyForCheck && monitor.ProviderTextForCheck.Contains("本软件已匹配：0")
                    && !monitor.ProviderTextForCheck.Contains("120 token") && monitor.ProviderTextForCheck.Contains("不计入")
                    && monitor.ProviderTextForCheck.Contains("非全部历史") && monitor.WalletTextForCheck.Contains("3.5000"),
                    "Usage monitor mixed wallet quota and tokens or omitted coverage.");
                foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
                {
                    ((App)Application.Current).Themes.Select(theme, save: false);
                    monitor.Width = monitor.MinWidth; monitor.Height = monitor.MinHeight;
                    await monitor.Dispatcher.InvokeAsync(monitor.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    PrototypeSmokeCheck.SavePreview(monitor, Path.Combine(output, theme + "-AI用量余额最小尺寸.png"));
                }
            }
            finally { monitor.Close(); }
            checks.Add("usage monitor polls without blocking UI and separates recent token scope from wallet in both themes");

            var monitorCancelled = false;
            var monitorStarted = new TaskCompletionSource();
            monitor = new(store, secrets, tasks) { Owner = main, QueryForCheck = async (_, _, token) =>
            {
                monitorStarted.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { monitorCancelled = true; throw; }
                throw new InvalidOperationException("Unexpected completion.");
            } };
            monitor.Show();
            var cancelDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            await monitorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            monitor.Close();
            while (!monitorCancelled && DateTimeOffset.UtcNow < cancelDeadline) await Task.Delay(10);
            Assert(monitorCancelled, "Closing usage monitor left a query running.");
            checks.Add("closing usage monitor cancels the outstanding read-only request");

            Reset(); pending = new(); client.Response = (_, _) => pending.Task;
            active = window.StartSortingAsync();
            window.Close(); pending.SetResult(GoodReply()); await active; Unchanged();
            Assert(window.CurrentOutcome is null, "Closed window accepted late result.");
            checks.Add("closed window cannot display or apply late result");

            var settingsBeforeInvalidSchema = File.ReadAllBytes(store.SettingsPath);
            File.WriteAllText(store.SettingsPath, "{\"SchemaVersion\":99,\"Settings\":{}}");
            var invalidSettingsWindow = Open();
            try
            {
                var callsBefore = client.Calls;
                await invalidSettingsWindow.StartSortingAsync();
                Assert(!invalidSettingsWindow.AutomaticButton.IsEnabled && invalidSettingsWindow.StatusText.Text.Contains("无法读取")
                    && client.Calls == callsBefore, "Invalid configuration crashed or issued a request.");
            }
            finally
            {
                invalidSettingsWindow.Close();
                // Restore the fixture bytes directly: the intentionally changed file must
                // be rejected by the original store's stale-write protection.
                File.WriteAllBytes(store.SettingsPath, settingsBeforeInvalidSchema);
            }
            checks.Add("unsupported settings schema reports error without request or crash");
        }
        finally { window.Close(); vm.MarkExported(); }
        await CheckDuplicateInstallationsAsync(main, output, checks);
        await CheckUpdateNotificationsAsync(main, output, checks);
        await CheckOfflineSortingAsync(main, output, checks);
        vm.MarkExported();
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        { Passed = true, RealAiRequest = false, RealGameRun = false, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task CheckOfflineSortingAsync(MainWindow main, string output, List<string> checks)
    {
        var root = Path.Combine(output, "离线 中文");
        var store = new AiSettingsStore(root);
        store.Save(AiSettings.Default);
        File.WriteAllText(store.SettingsPath, "{ invalid AI settings");
        new AutomaticSortingPreferencesStore(root).SaveMode("offline", true);
        ModInstallation Mod(string id, params string[] deps) => new(Path.Combine(root, id), id, ModSource.Local, "",
            new(id, "test", id, ["1.7"], "", new(deps.Select(d => new ModDependency(d, d)).ToArray(), [], [], []), false), []);
        var vm = main.ViewModel;
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("<ModsConfigData><version>1.7</version><activeMods><li>child</li></activeMods></ModsConfigData>"));
        var document = new ModsConfigXmlFormat().Read(input);
        vm.Load(document, Path.Combine(root, "ModsConfig.xml"));
        vm.ApplyScan(new(new(null, "", ""), GameVersion.Parse("1.7"), new([Mod("child", "parent"), Mod("parent")]), []), observeActivity: false);
        vm.SetCustomRules([]);
        var secrets = new SecretCounter();
        var client = new FakeClient { Response = (_, _) => throw new Exception("Offline called AI") };
        var notice = "";
        var passed = false;
        var window = new AiSortingWindow(vm, root, client: client, secrets: secrets,
            gamePaths: () => throw new Exception("Offline accessed game launcher"),
            prepareSave: () => throw new Exception("Offline saved game config")) { Owner = main,
            OfflineResultNotice = (message, success) => { notice = message; passed = success; } };
        window.Show();
        try
        {
            Assert(window.AutomaticButton.IsEnabled && window.PermissionBox.SelectedIndex == 3, "Offline blocked on AI settings");
            await window.StartAutomaticAsync();
            Assert(passed && notice.Contains("未进行游戏测试") && vm.Rows.Select(r => r.PackageId).SequenceEqual(["parent", "child"]), "Offline result not applied or notice absent");
            Assert(client.Calls == 0 && secrets.Reads == 0 && window.TokenText.Text.Contains("0 token") && !File.Exists(Path.Combine(root, "ModsConfig.xml")), "Offline used AI or saved config");
            vm.UndoCommand.Execute(null);
            Assert(vm.Rows.Select(r => r.PackageId).SequenceEqual(["child"]), "Offline undo failed");
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, theme + "-离线排序.png"));
            }
        }
        finally { window.Close(); vm.MarkExported(); }
        checks.Add("offline bypasses corrupt AI settings, credentials and game lifecycle; adds installed prerequisites; result notice and undo");
    }

    private static async Task CheckUpdateNotificationsAsync(MainWindow main, string output, List<string> checks)
    {
        var themes = ((App)Application.Current).Themes;
        var originalTheme = themes.Preference;
        var order = main.ViewModel.Rows.Select(row => row.PackageId).ToArray();
        try
        {
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                themes.Select(theme, save: false);
                main.SearchBox.Focus();
                var focus = System.Windows.Input.Keyboard.FocusedElement;
                main.ShowUpdateNotification("更新检测失败", "无法连接 GitHub，请稍后重试。当前功能可继续使用。", true);
                main.UpdateLayout();
                Assert(main.UpdateNotice.IsVisible && main.UpdateRetryButton.IsVisible && !main.UpdateInstallButton.IsVisible,
                    "Update failure does not offer the expected retry action.");
                Assert(ReferenceEquals(focus, System.Windows.Input.Keyboard.FocusedElement), "Update notice stole keyboard focus.");
                PrototypeSmokeCheck.SavePreview(main, Path.Combine(output, theme + "-update-failure.png"));
                main.ShowUpdateNotification("新版本 0.5.2 已下载", "点击更新并重启，即可自动安装并打开新版。", false, ready: true);
                main.UpdateLayout();
                Assert(!main.UpdateRetryButton.IsVisible && main.UpdateInstallButton.IsVisible,
                    "Verified update does not offer its install action.");
                Assert(main.UpdateInstallButton.Content.ToString() == "更新并重启", "Update action label is incorrect.");
                Assert(main.UpdateNotice.ActualWidth <= main.ActualWidth && main.UpdateNotice.ActualHeight < main.ActualHeight / 2,
                    "Update notice obscures the main window.");
                Assert(ReferenceEquals(focus, System.Windows.Input.Keyboard.FocusedElement), "Download notification stole keyboard focus.");
                PrototypeSmokeCheck.SavePreview(main, Path.Combine(output, theme + "-update-ready.png"));
                main.UpdateDismissButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(main.UpdateNotice.Visibility == Visibility.Collapsed, "Update notice cannot be dismissed.");
                await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            Assert(order.SequenceEqual(main.ViewModel.Rows.Select(row => row.PackageId)), "Update notices changed mod order.");
            checks.Add("update notifications support both themes, retry, verified download and dismissal without focus or order changes");
        }
        finally { themes.Select(originalTheme, save: false); main.UpdateNotice.Visibility = Visibility.Collapsed; }
    }

    private static async Task CheckDuplicateInstallationsAsync(MainWindow main, string output, List<string> checks)
    {
        var root = Path.Combine(output, "重复安装 中文", Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(root, "Mods");
        foreach (var folder in new[] { "一", "二" })
        {
            var about = Path.Combine(mods, folder, "About");
            Directory.CreateDirectory(about);
            File.WriteAllText(Path.Combine(about, "About.xml"),
                $"<ModMetaData><name>中文样例 {folder}</name><packageId>sample.mod</packageId><modVersion>{(folder == "一" ? "2.0" : "1.0")}</modVersion><supportedVersions><li>1.7</li></supportedVersions></ModMetaData>");
        }
        var paths = new GamePaths { LocalModsDirectory = mods, TargetVersionOverride = "1.7" };
        var scanner = new ModScanner();
        var scan = scanner.Scan(paths, inspectContent: false);
        var vm = main.ViewModel;
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes(
            "<ModsConfigData><version>1.7</version><activeMods><li>sample.mod</li></activeMods></ModsConfigData>"));
        vm.Load(new ModsConfigXmlFormat().Read(xml), Path.Combine(root, "ModsConfig.xml"));
        vm.ApplyScan(scan, observeActivity: false);
        var expected = vm.Rows.Select(row => row.PackageId).ToArray();
        var duplicates = DuplicateInstallations.FindRequired(scan.Catalogue, expected, new(1, 7));
        Assert(duplicates.Count == 1 && duplicates[0].Candidates.Count == 2, "Duplicate fixture scan lost installations.");
        new AiSettingsStore(root).Save(AiSettings.Default with { Enabled = true, Model = "synthetic-model" });
        var secrets = new SecretCounter();
        var client = new FakeClient { Response = (_, _) => throw new Exception("Preflight sent AI request") };
        var automatic = new AiSortingWindow(vm, root, client: client, secrets: secrets, gamePaths: () => paths) { Owner = main };
        automatic.Show();
        try
        {
            var observed = false;
            async Task CancelDuplicateDialogAsync()
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
                DuplicateModsWindow? modal;
                while ((modal = automatic.OwnedWindows.OfType<DuplicateModsWindow>().SingleOrDefault()) is null)
                {
                    if (DateTimeOffset.UtcNow >= deadline) throw new InvalidOperationException("Duplicate preflight dialog missing.");
                    await Task.Delay(10);
                }
                observed = modal.InstallationsGrid.Items.Count == 2;
                PrototypeSmokeCheck.SavePreview(modal, Path.Combine(output, "duplicate-installations.png"));
                modal.Close();
            }
            var cancel = CancelDuplicateDialogAsync();
            await automatic.StartAutomaticAsync();
            await cancel;
            Assert(observed && !automatic.IsWorking && client.Calls == 0 && secrets.Reads == 0
                && expected.SequenceEqual(vm.Rows.Select(row => row.PackageId)), "Preflight cancel changed order or sent request.");
            checks.Add("automatic sorting shows duplicate names, sources and paths before secret access; cancel preserves order");

            var remaining = await automatic.RefreshDuplicateInstallationsAsync(CancellationToken.None);
            Assert(remaining.Count == 1 && expected.SequenceEqual(vm.Rows.Select(row => row.PackageId)), "Refresh lost duplicates or order.");
            checks.Add("duplicate refresh preserves current order and still blocks unresolved duplicates");
            var ranked = DuplicateCopyInspector.Rank(duplicates[0].Candidates.Select(mod =>
                DuplicateCopyInspector.Inspect(mod, new(1, 7), new HashSet<string>(), default)));
            DuplicateCopyInspection? selectedCopy = null;
            var selection = new DuplicateModsWindow(remaining, automatic.RefreshDuplicateInstallationsAsync,
                (_, selected, _) => { selectedCopy = selected; return Task.FromResult(false); }, new(1, 7)) { Owner = automatic };
            selection.Show();
            try
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (!selection.ChooseButton.IsEnabled && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
                Assert(selection.ChooseButton.IsEnabled && ((DuplicateModRow)selection.InstallationsGrid.Items[0]).Recommended,
                    "Duplicate recommendations were not shown.");
                selection.InstallationsGrid.SelectedIndex = 1;
                selection.ChooseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(selectedCopy?.Installation.Directory == ranked[1].Installation.Directory
                    && Directory.Exists(ranked[0].Installation.Directory) && Directory.Exists(ranked[1].Installation.Directory),
                    "User alternative was ignored or selection moved files without consent.");
                selection.Width = selection.MinWidth; selection.Height = selection.MinHeight;
                foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
                {
                    ((App)Application.Current).Themes.Select(theme, save: false);
                    await selection.Dispatcher.InvokeAsync(selection.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var bounds = new Rect(selection.ChooseButton.TranslatePoint(new Point(), selection), selection.ChooseButton.RenderSize);
                    Assert(bounds.Right <= selection.ActualWidth && bounds.Bottom <= selection.ActualHeight,
                        "Copy choice button clipped.");
                    PrototypeSmokeCheck.SavePreview(selection, Path.Combine(output, theme + "-duplicate-copy-choice.png"));
                }
            }
            finally { selection.Close(); }
            checks.Add("duplicate recommendations allow a different row; both themes fit minimum window and selection alone never moves files");

            DispatcherOperation Answer(Window owner, MessageBoxResult answer) => main.Dispatcher.InvokeAsync(() =>
            {
                var dialog = owner.OwnedWindows.OfType<MessageDialogWindow>().Single();
                ((Button)dialog.ButtonsPanel.Children.Cast<Button>().Single(button => Equals(button.Tag, answer)))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, DispatcherPriority.ApplicationIdle);
            var no = Answer(automatic, MessageBoxResult.No);
            Assert(!await automatic.ChooseDuplicateCopyAsync(ranked, ranked[1], default), "Decline accepted copy.");
            await no;
            var backups = new DuplicateCopyBackups(root, paths);
            Assert(backups.List().Count == 0 && ranked.All(c => Directory.Exists(c.Installation.Directory)), "Decline moved a copy.");
            checks.Add("actual move confirmation No leaves every installation and journal unchanged");

            var yes = Answer(automatic, MessageBoxResult.Yes);
            Assert(await automatic.ChooseDuplicateCopyAsync(ranked, ranked[1], default), "Confirmed alternative rejected.");
            await yes;
            await automatic.RefreshDuplicateInstallationsAsync(default);
            Assert(backups.List().Single().Selected == ranked[1].Installation.Directory
                && vm.Rows.Single().Match?.UniqueInstallation?.Directory == ranked[1].Installation.Directory
                && expected.SequenceEqual(vm.Rows.Select(row => row.PackageId)), "Confirmed copy or order mismatch.");
            checks.Add("actual move confirmation Yes uses chosen lower copy, records backup, refreshes and preserves order");

            var restore = new DuplicateCopyBackupsWindow(backups, automatic.RefreshDuplicateInstallationsAsync) { Owner = automatic };
            restore.Show();
            try
            {
                var decline = Answer(restore, MessageBoxResult.No);
                await restore.RestoreAsync(); await decline;
                Assert(backups.List().Count == 1 && !Directory.Exists(ranked[0].Installation.Directory), "Restore decline mutated files.");
                var accept = Answer(restore, MessageBoxResult.Yes);
                await restore.RestoreAsync(); await accept;
                Assert(backups.List().Count == 0 && ranked.All(c => Directory.Exists(c.Installation.Directory))
                    && expected.SequenceEqual(vm.Rows.Select(row => row.PackageId)), "Restore lost files or current order.");
            }
            finally { restore.Close(); }
            checks.Add("backup recovery requires confirmation and restores both Chinese-path copies without changing order");
            remaining = await automatic.RefreshDuplicateInstallationsAsync(default);
            Directory.Move(Path.Combine(mods, "二"), Path.Combine(root, "已移出副本"));
            var resolved = new DuplicateModsWindow(remaining, automatic.RefreshDuplicateInstallationsAsync) { Owner = automatic };
            var refresh = main.Dispatcher.InvokeAsync(() => resolved.RefreshAsync(), DispatcherPriority.ApplicationIdle);
            Assert(resolved.ShowDialog() == true, "Resolved duplicates did not continue.");
            await await refresh;
            Assert(vm.Rows.Single().Match?.UniqueInstallation is not null
                && expected.SequenceEqual(vm.Rows.Select(row => row.PackageId)) && client.Calls == 0,
                "Resolution changed order or used AI.");
            checks.Add("after duplicate directory is moved outside Mods, refresh closes dialog and keeps order");

            var local = vm.CurrentCatalogue!.Installations.Single();
            var workshop = local with { Source = ModSource.Workshop, Directory = Path.Combine(root, "工坊"), FolderName = "123", WorkshopId = "123" };
            vm.ApplyScan(scan with { Catalogue = new([local, workshop]) }, observeActivity: false);
            var available = vm.AvailableMods.Single();
            Assert(available.Installation.Source == ModSource.Workshop && vm.AddAvailableRows([available], vm.Rows.Count)
                && vm.Rows.Last().PackageId == "sample.mod_steam"
                && vm.Rows.Last().Match?.UniqueInstallation?.Directory == workshop.Directory,
                "Adding selected Workshop copy resolved to wrong source.");
            vm.UndoCommand.Execute(null);
            Assert(expected.SequenceEqual(vm.Rows.Select(row => row.PackageId)) && vm.AvailableMods.Count == 1,
                "Workshop alias addition undo lost original order.");
            checks.Add("adding Workshop copy uses Steam alias, export preserves it, and undo restores local-only order");
        }
        finally { automatic.Close(); vm.MarkExported(); }
    }

    private static async Task CheckDatabaseWindowAsync(MainWindow main, string root, string output, List<string> checks)
    {
        IProgress<string>? progress = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var databaseWindow = new DatabaseWindow(root, async (updates, token) =>
        {
            progress = updates;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Synthetic update must be cancelled.");
        }) { Owner = main };
        databaseWindow.Show();
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!databaseWindow.UpdateButton.IsEnabled && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
            Assert(databaseWindow.DetailsText.Text.Contains("631"), "Offline database did not load in the window.");
            databaseWindow.Width = databaseWindow.MinWidth; databaseWindow.Height = databaseWindow.MinHeight;
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                await databaseWindow.Dispatcher.InvokeAsync(databaseWindow.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var button = databaseWindow.UpdateButton;
                var bounds = new Rect(button.TranslatePoint(new Point(), databaseWindow), button.RenderSize);
                Assert(bounds.Right <= databaseWindow.ActualWidth && bounds.Bottom <= databaseWindow.ActualHeight,
                    "Database update button is clipped at minimum size.");
                PrototypeSmokeCheck.SavePreview(databaseWindow, Path.Combine(output, theme + "-社区数据库最小尺寸.png"));
            }
            var update = databaseWindow.UpdateAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!update.IsCompleted && databaseWindow.CancelButton.IsEnabled && !databaseWindow.UpdateButton.IsEnabled,
                "Database update blocked the dispatcher or permitted a duplicate update.");
            databaseWindow.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await update;
            progress!.Report("迟到的下载进度");
            await databaseWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(databaseWindow.StatusText.Text.Contains("已取消") && databaseWindow.UpdateButton.IsEnabled,
                "Late database progress replaced cancellation or update remained disabled.");
        }
        finally { databaseWindow.Close(); }
        checks.Add("offline database window fits both themes and updates asynchronously with cancellation and stale-progress isolation");

        var localRoot = Path.Combine(root, "数据库来源 设置");
        var store = new DatabaseStore(localRoot);
        var disabled = new DatabaseSource(DatabaseSourceMode.Disabled, "");
        await store.ApplyAsync(new(disabled, disabled), false, false, null, default);
        Directory.CreateDirectory(localRoot);
        var rulesFile = Path.Combine(localRoot, "社区 规则.json");
        var workshopFile = Path.Combine(localRoot, "工坊 数据.json");
        File.WriteAllText(rulesFile, "{\"rules\":{\"b.mod\":{\"loadAfter\":{\"a.mod\":{}}}}}");
        File.WriteAllText(workshopFile, "{\"database\":{\"123\":{\"packageId\":\"a.mod\",\"gameVersions\":[\"1.7\"]}}}");
        databaseWindow = new DatabaseWindow(localRoot) { Owner = main };
        databaseWindow.Show();
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!databaseWindow.UpdateButton.IsEnabled && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
            var community = databaseWindow.CommunityEditor;
            var workshop = databaseWindow.WorkshopEditor;
            Assert(community.DisabledChoice.IsChecked == true && workshop.DisabledChoice.IsChecked == true,
                "Disabled database modes were lost on restart.");
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                community.GitHubChoice.IsChecked = true;
                Assert(community.GitHubInput.IsEnabled && community.GitHubDownload.IsEnabled
                    && !community.WebsiteInput.IsEnabled && !community.LocalInput.IsEnabled, "GitHub mode controls failed.");
                community.WebsiteChoice.IsChecked = true;
                Assert(community.WebsiteInput.IsEnabled && community.WebsiteDownload.IsEnabled
                    && !community.GitHubDownload.IsEnabled, "Website mode controls failed.");
                community.LocalChoice.IsChecked = true;
                Assert(community.LocalInput.IsEnabled && community.ChooseFile.IsEnabled && community.ReadFile.IsEnabled
                    && !community.WebsiteDownload.IsEnabled, "Local mode controls failed.");
                community.DisabledChoice.IsChecked = true;
                Assert(!community.GitHubInput.IsEnabled && !community.WebsiteInput.IsEnabled && !community.LocalInput.IsEnabled
                    && workshop.DisabledChoice.IsChecked == true, "Radio groups interfered or disabled source allowed reads.");
            }
            community.LocalChoice.IsChecked = true; community.LocalInput.Text = rulesFile;
            workshop.LocalChoice.IsChecked = true; workshop.LocalInput.Text = workshopFile;
            await databaseWindow.UpdateAsync();
            Assert(store.LoadSettings().Community.LocalFile == rulesFile && store.Load().Snapshot.RulePackages == 1
                && store.Load().Snapshot.IndexedItems == 1, "Local sources did not apply and persist.");
            workshop.WebsiteChoice.IsChecked = true;
            workshop.WebsiteInput.Text = "unsaved invalid address";
            File.WriteAllText(rulesFile, "{\"rules\":{\"c.mod\":{\"loadAfter\":{\"a.mod\":{}}}}}");
            var oldId = store.Load().Snapshot.Id;
            community.ReadFile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!databaseWindow.UpdateButton.IsEnabled && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
            Assert(store.Load().Snapshot.Id != oldId && store.LoadSettings().Workshop.Mode == DatabaseSourceMode.LocalFile
                && workshop.WebsiteInput.Text == "unsaved invalid address" && workshop.WebsiteChoice.IsChecked == true,
                "Section read applied or discarded unsaved edits in the other source.");
            workshop.LocalChoice.IsChecked = true;
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                await databaseWindow.Dispatcher.InvokeAsync(databaseWindow.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert(community.LocalChoice.IsChecked == true && community.LocalInput.Text == rulesFile,
                    "Theme switch lost database selections.");
                PrototypeSmokeCheck.SavePreview(databaseWindow, Path.Combine(output, theme + "-数据库来源配置.png"));
            }
        }
        finally { databaseWindow.Close(); }
        checks.Add("independent source modes, Unicode local reads, persistent configuration, isolated section actions and theme hot switching");
    }

    private static void MustReject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Stale result was accepted.");
    }
    private static void MustRejectCheckpoint(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid checkpoint was accepted.");
    }
    private static DependencyObject? FindChild(DependencyObject parent, Type type)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (type.IsInstanceOfType(child)) return child;
            if (FindChild(child, type) is { } found) return found;
        }
        return null;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class SecretCounter : ISecretStore
    {
        internal int Reads;
        public string? Read(string name) { Reads++; return "synthetic-key"; }
        public void Write(string name, string value) => throw new InvalidOperationException("Unexpected secret write.");
        public void Delete(string name) => throw new InvalidOperationException("Unexpected secret delete.");
    }
    private sealed class FakeClient : IAiClient
    {
        internal int Calls;
        internal required Func<AiRequest, CancellationToken, Task<AiClientResult>> Response;
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken cancellationToken)
        { Calls++; return Response(request, cancellationToken); }
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? apiKey, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unexpected connection test.");
    }
}
#endif
