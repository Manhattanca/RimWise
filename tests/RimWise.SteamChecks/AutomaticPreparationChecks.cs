using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Game;
using RimWise.Integration.Steam;
using System.Reflection;

internal static class AutomaticPreparationChecks
{
    private static readonly GameVersion Version = new(1, 7);
    private static ModInstallation Mod(string id, string[]? dependencies = null, string[]? incompatible = null) => new(
        Path.Combine(AppContext.BaseDirectory, "准备检查 中文", id), id, ModSource.Local, "",
        new(id, "test", id, ["1.7"], "", new((dependencies ?? []).Select(d => new ModDependency(d, d)).ToArray(),
            [], [], incompatible ?? []), false), []);
    private static void Assert(bool value) { if (!value) throw new Exception("Automatic preparation regression"); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Unauthorized removal accepted");
    }
    internal static void Run(Action<string, Action> check)
    {
        check("Download failures preserve only unresolved opaque tokens", () =>
        {
            var duplicate = Mod("test.duplicate");
            var catalogue = new ModCatalogue([Mod("test.readable"), duplicate,
                duplicate with { Directory = duplicate.Directory + "-other" }],
                ["test.opaque", "test.readable", "test.duplicate"]);
            var calls = 0;
            Task<IReadOnlyList<WorkshopItemState>> Fail(string _, CancellationToken token)
            { calls++; throw new IOException("synthetic offline"); }
            var result = AutomaticModDownloads.RunAsync([new("test.download", "123")], catalogue,
                Version, AppContext.BaseDirectory, Fail,
                (_, _) => throw new Exception("Failed enqueue must not wait"), default).GetAwaiter().GetResult();
            Assert(calls == 1 && result.Failures.ContainsKey("test.download"));
            Assert(result.Catalogue.UnverifiedOrderIdentifiers.SequenceEqual(["test.opaque"]));
            Assert(result.Catalogue.Resolve("test.opaque", Version).Candidates.Count == 0
                && result.Catalogue.Resolve("test.duplicate", Version).Candidates.Count == 2);
        });
        check("Workshop reread recovers exact previous identity without dependencies or opaque rebinding", () =>
        {
            var root = Path.Combine(AppContext.BaseDirectory, "metadata-download 中文", Guid.NewGuid().ToString("N"));
            var directory = Path.Combine(root, "123");
            Directory.CreateDirectory(Path.Combine(directory, "About"));
            File.WriteAllText(Path.Combine(directory, "About", "About.xml"), "<ModMetaData><broken>");
            var previous = Mod("test.download", ["test.old.dependency"]) with
                { Directory = directory, FolderName = "123", Source = ModSource.Workshop, WorkshopId = "123" };
            Task<IReadOnlyList<WorkshopItemState>> Complete(string id, CancellationToken token)
                => Task.FromResult<IReadOnlyList<WorkshopItemState>>([new(id, true, true, directory)]);
            var result = AutomaticModDownloads.RunAsync([new("test.download", "123")],
                new([previous], ["test.opaque", "test.download"]), Version, root, Complete, Complete, default)
                .GetAwaiter().GetResult();
            var mod = result.Catalogue.Resolve("test.download", Version).UniqueInstallation;
            Assert(result.Failures.Count == 0 && mod is { Metadata: null, RecoveredIdentity: not null });
            Assert(mod!.Diagnostics.Any(d => d.Code == "MetadataUnreadable"));
            Assert(result.Catalogue.UnverifiedOrderIdentifiers.SequenceEqual(["test.opaque"])
                && result.Catalogue.Resolve("test.opaque", Version).Candidates.Count == 0);
            Assert(AiOrderPlanner.ExpandRequiredOrder(["test.download"], result.Catalogue, Version)
                .SequenceEqual(["test.download"]));
        });
        check("Dependency callback scope loss never schedules an opaque token and resolved scope is retired", () =>
        {
            var owner = Mod("test.owner") with { Metadata = Mod("test.owner").Metadata! with
                { Requirements = new([new("test.base", "Base", "123")], [], [], []) } };
            var calls = 0;
            var result = RequiredDependencyPreparation.RunAsync(new([owner], ["test.opaque"]),
                ["test.owner", "test.opaque"], [], Version, null, [], (source, pending, _) =>
                {
                    calls++;
                    Assert(pending.Count == 1 && pending[0].Identifier == "test.base");
                    var duplicate = Mod("test.duplicate");
                    return Task.FromResult(new AutomaticDownloadResult(new([owner, Mod("test.base"), duplicate,
                        duplicate with { Directory = duplicate.Directory + "-other" }], ["test.callback", "test.base", "test.duplicate"]),
                        new Dictionary<string, string>()));
                }, default).GetAwaiter().GetResult();
            Assert(calls == 1 && result.Failures.Count == 0);
            Assert(result.Catalogue.UnverifiedOrderIdentifiers.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(["test.opaque", "test.callback"]));
            Assert(result.Catalogue.Resolve("test.opaque", Version).Candidates.Count == 0
                && result.Catalogue.Resolve("test.duplicate", Version).Candidates.Count == 2);
        });
        check("Both test installation gates allow zero-candidate tokens but reject scoped duplicates", () =>
        {
            var root = Path.Combine(AppContext.BaseDirectory, "metadata-gates 中文", Guid.NewGuid().ToString("N"));
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
            // Constructor-only fixture: this file is never launched.
            File.WriteAllText(Path.Combine(game, "RimWorldWin64.exe"), "synthetic fixture, never execute");
            var profile = Path.Combine(root, "profile"); Directory.CreateDirectory(profile);
            var personal = Path.Combine(profile, "ModsConfig.xml"); File.WriteAllText(personal, "<ModsConfigData />");
            var options = new OrdinaryGameTestOptions(game, personal, Path.Combine(root, "test-output"), Version);
            var gate = typeof(AutomaticGameMaintenance).GetMethod("ValidateInstallations", BindingFlags.Static | BindingFlags.NonPublic)!;
            void Validate(ModCatalogue catalogue, string id)
            {
                try { gate.Invoke(null, [catalogue, new[] { id }, Version]); }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
                    { throw (InvalidDataException)ex.InnerException; }
            }
            var opaque = new ModCatalogue([], ["test.opaque"]);
            Validate(opaque, "test.opaque");
            _ = new OrdinaryGameTestSession(options, opaque, ["test.opaque"], [], []);
            Reject(() => Validate(new([]), "test.missing"));
            Reject(() => { _ = new OrdinaryGameTestSession(options, new([]), ["test.missing"], [], []); });
            var mod = Mod("test.duplicate");
            var duplicates = new ModCatalogue([mod, mod with { Directory = mod.Directory + "-other" }], [mod.PackageId]);
            Reject(() => Validate(duplicates, mod.PackageId));
            Reject(() => { _ = new OrdinaryGameTestSession(options, duplicates, [mod.PackageId], [], []); });
        });
        check("Self dependency and self ordering do not produce impossible cycles", () =>
        {
            var self = Mod("test.self", ["TEST.SELF"]);
            self = self with { Metadata = self.Metadata! with { Requirements = self.Metadata.Requirements with
                { LoadBefore = ["test.self"], LoadAfter = ["TEST.SELF"] } } };
            var catalogue = new ModCatalogue([Mod("test.other"), self]);
            string[] order = ["test.other", "test.self"];
            var sorted = new LoadOrderSorter().Sort(catalogue, order, Version, Version, deferAdvisoryCycles: true);
            Assert(sorted.Complete && sorted.Order.SequenceEqual(order));
            Assert(sorted.Diagnostics.Any(d => d.Code == "SelfRequirementIgnored")
                && !sorted.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error));
            Assert(new LoadOrderSorter().Sort(new([Mod("test.a", ["test.b"]), Mod("test.b", ["test.a"])]),
                ["test.a", "test.b"], Version, Version, deferAdvisoryCycles: true).Complete);
            var workshop = self with { Source = ModSource.Workshop, WorkshopId = "123", Directory = self.Directory + "-steam" };
            Assert(new LoadOrderSorter().Sort(new([self, workshop]), ["test.self_steam"], Version, Version, deferAdvisoryCycles: true).Complete);
        });
        check("Graph validation accepts a valid code preference without allowing arbitrary original moves", () =>
        {
            var catalogue = new ModCatalogue([Mod("old.a", ["old.b"]), Mod("old.b"), Mod("new.mod")]);
            var rules = InsertionScope.Rules([], InsertionScope.Capture(["old.a", "old.b"]));
            string[] target = ["old.b", "new.mod", "old.a"];
            Assert(new LoadOrderSorter().Sort(catalogue, target, Version, Version, rules, true).Changed);
            var plan = AiOrderPlanner.PreparedOrder(["old.a", "old.b", "new.mod"], target, catalogue,
                ["old.a", "old.b"], Version, rules, []);
            Assert(plan.CanApply && plan.Rejected.Count == 0 && plan.After.SequenceEqual(target));
            Assert(!new LoadOrderSorter().Validate(catalogue, ["old.a", "new.mod", "old.b"], Version, Version, rules, true).Complete);
            Assert(!new LoadOrderSorter().Validate(new([Mod("old.a"), Mod("old.b"), Mod("new.mod")]), target,
                Version, Version, rules, true).Complete);
        });
        check("Two hundred prepared graphs validate despite alternative code tie preferences", () =>
        {
            var random = new Random(241103);
            for (var round = 0; round < 200; round++)
            {
                string[] ids = ["old.a", "old.b", "old.c", "new.x", "new.y", "new.z"];
                var graphOrder = ids.OrderBy(_ => random.Next()).ToArray();
                var catalogue = new ModCatalogue(graphOrder.Select((id, i) => Mod(id,
                    graphOrder.Take(i).Where(_ => random.Next(4) == 0).ToArray())).ToArray());
                var rules = InsertionScope.Rules([], InsertionScope.Capture(ids.Take(3)));
                var sorted = new LoadOrderSorter().Sort(catalogue, ids, Version, Version, rules, true,
                    ids.OrderBy(_ => random.Next()).ToArray());
                Assert(sorted.Complete);
                Assert(new LoadOrderSorter().Validate(catalogue, sorted.Order, Version, Version, rules, true).Complete);
                Assert(AiOrderPlanner.PreparedOrder(ids, sorted.Order, catalogue, ids.Take(3).ToArray(), Version, rules, []).Rejected.Count == 0);
            }
        });
        check("Player retention override survives replay publication and insertion recovery", () =>
        {
            string[] order = ["test.a", "test.b", "test.child"];
            var catalogue = new ModCatalogue([Mod("test.a", incompatible: ["test.b"]), Mod("test.b"), Mod("test.child", ["test.a"])]);
            var conflict = DeclaredConflictResolution.Find(catalogue, order, order, Version)!;
            Assert(AutomaticRemovalChoice.Select(conflict) is null);
            Assert(!ConflictChoicePreview.Create(conflict, ["test.a"]).CanContinue);
            var preview = ConflictChoicePreview.Create(conflict, ["test.a"], true);
            Assert(preview.CanContinue && preview.RemovedIds.SequenceEqual(["test.a", "test.child"])
                && preview.RetentionOverrideIds.Length == 2);
            var approval = JsonSerializer.Deserialize<ConflictRemovalApproval>(JsonSerializer.Serialize(preview.Approve(conflict)))!;
            var remaining = DeclaredConflictResolution.Replay(catalogue, order, order, Version, [approval]);
            Assert(remaining.SequenceEqual(["test.b"]));
            Assert(AiOrderPlanner.PreparedOrder(order, remaining, catalogue, order, Version, [], [approval]).CanApply);
            Reject(() => DeclaredConflictResolution.Replay(catalogue, order, order, Version, [approval with { RetentionOverrideIds = [] }]));
            Reject(() => DeclaredConflictResolution.Replay(catalogue, order, order, Version, [approval with { RetentionOverrideIds = ["test.a"] }]));
            var scope = InsertionScope.Capture(order);
            InsertionScope.ValidateOrder(scope, remaining, approval.RemovedIds);
            Reject(() => InsertionScope.ValidateOrder(scope, remaining));
            new AiTaskCheckpoint(AiTaskKind.Maintenance, AiTaskPhase.Paused, new('A', 64), order, remaining, DateTimeOffset.UtcNow)
                { Limits = new() { InsertionBaselineJson = scope }, DeadlineUtc = DateTimeOffset.MaxValue,
                    ConflictApprovals = [approval] }.Validate();
        });
        check("Automatic retention conflicts ask the player; official content stays protected", () =>
        {
            string[] order = ["test.a", "test.b"];
            var catalogue = new ModCatalogue([Mod("test.a", incompatible: ["test.b"]), Mod("test.b")]);
            var conflict = DeclaredConflictResolution.Find(catalogue, order, order, Version)!;
            var root = Path.Combine(AppContext.BaseDirectory, "冲突确认 中文", Guid.NewGuid().ToString("N"));
            using var task = new AiTaskRecoveryStore(root).Create(new(AiTaskKind.Maintenance, AiTaskPhase.Prepared,
                new('A', 64), order, order, DateTimeOffset.UtcNow)
                { Limits = new() { ConfirmModRemoval = false }, DeadlineUtc = DateTimeOffset.MaxValue });
            var asked = 0;
            var approval = AutomaticGameMaintenance.ChooseAndRecordAsync(conflict, task, (_, _) =>
                { asked++; return Task.FromResult<string[]?>(["test.b"]); }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(asked == 1 && approval.RetentionOverrideIds.SequenceEqual(["test.b"]));
            var core = Mod("ludeon.rimworld") with { Source = ModSource.Game };
            conflict = DeclaredConflictResolution.Find(new([core, Mod("test.a", incompatible: ["ludeon.rimworld"])]),
                ["ludeon.rimworld", "test.a"], ["ludeon.rimworld"], Version)!;
            Assert(!conflict.Options.Single(o => o.Identifier == "ludeon.rimworld").CanOverrideRetention
                && !ConflictChoicePreview.Create(conflict, ["ludeon.rimworld"], true).CanContinue);
        });
    }
}
