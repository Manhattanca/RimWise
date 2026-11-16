using RimWise.Core;
using RimWise.Integration.AI;

internal static class OrderingCycleChecks
{
    private static readonly GameVersion Version = new(1, 7);
    private static readonly string[] Original = ["cycle.a", "cycle.b", "other.mod"];
    private static readonly CustomOrderRule[] Rules = [new("cycle.a", "cycle.b"), new("cycle.b", "cycle.a")];

    internal static void Run(string scratch, Action<string, Action> check)
    {
        ModInstallation Mod(string id, string[]? dependencies = null) => new(Path.Combine(scratch, id), id, ModSource.Local, "",
            new("名称 " + id, "fixture", id, ["1.7"], "", new((dependencies ?? []).Select(d => new ModDependency(d, d)).ToArray(), [], [], []), false), []);
        var catalogue = new ModCatalogue(Original.Select(id => Mod(id)).ToArray());
        string Root() => Path.Combine(scratch, "cycle-tests", Guid.NewGuid().ToString("N"));
        AiRequest Request(string[]? retained = null) => new(AiSettings.Default with { Enabled = true, Model = "cycle-fixture" }, null,
            "1.7", Original, Original, retained ?? [], []);
        MaintenanceResult Run(Session game, Client client, string? root = null, string? resume = null,
            MaintenanceLimits? limits = null, string[]? retained = null, CancellationToken token = default,
            ModCatalogue? mods = null, IReadOnlyList<CustomOrderRule>? rules = null) =>
            new LevelOneMaintenance(client, new(root ?? Root()), game, resume).RunAsync(Request(retained), mods ?? catalogue,
                rules ?? Rules, limits ?? new(), null, token).GetAwaiter().GetResult();

        check("error-free cyclic order stays unchanged without AI or exclusions", () =>
        {
            var game = new Session(catalogue); var client = new Client();
            var result = Run(game, client);
            Assert(result.Outcome == MaintenanceOutcome.Passed && game.Tested.Single().SequenceEqual(Original)
                && result.TrialExclusions.Count == 0 && client.Calls == 0 && game.Accepted);
        });
        check("related cyclic error swaps first and passes without AI or removal", () =>
        {
            var game = new Session(catalogue) { Report = ids => ids[0] == "cycle.a" ? Error("cycle.a") : new(true, []) };
            var client = new Client(); var result = Run(game, client);
            Assert(result.Outcome == MaintenanceOutcome.Passed && client.Calls == 0 && game.Tested.Count == 2
                && result.LastAttemptedOrder.SequenceEqual(["cycle.b", "cycle.a", "other.mod"])
                && result.TrialExclusions.Count == 0 && game.Accepted);
        });
        check("failed swap tries one related member then cumulatively handles remaining red errors", () =>
        {
            var game = new Session(catalogue) { Report = ids => ids.Contains("cycle.a") ? Error("cycle.a")
                : ids.Contains("cycle.b") ? Error("cycle.b") : new(true, []) };
            var client = new Client(); var result = Run(game, client);
            Assert(result.Outcome == MaintenanceOutcome.Passed && client.Calls == 0 && game.Tested.Count == 4
                && game.Tested[1].SequenceEqual(["cycle.b", "cycle.a", "other.mod"])
                && game.Tested[2].SequenceEqual(["cycle.b", "other.mod"])
                && result.TrialExclusions.Select(t => t.SelectedId).SequenceEqual(["cycle.a", "cycle.b"])
                && result.TrialExclusions.All(t => t.RemovedIds.Length == 1) && result.OriginalRestored && !game.Accepted);
            var plan = AiOrderPlanner.ConfirmedTrialOrder(Original, result.LastAttemptedOrder, catalogue, [], Version,
                Rules, [], result.TrialExclusions, true);
            Assert(plan.CanApply && plan.After.SequenceEqual(["other.mod"]));
        });
        check("unrelated red error does not swap or remove cyclic members", () =>
        {
            var game = new Session(catalogue) { Report = ids => ids.Contains("other.mod") ? Error("other.mod") : new(true, []) };
            var client = new Client(); var result = Run(game, client);
            Assert(result.Outcome == MaintenanceOutcome.Passed && client.Calls == 1 && game.Tested.Count == 2
                && result.TrialExclusions.Single().SelectedId == "other.mod"
                && game.Tested.All(ids => ids.Where(id => id.StartsWith("cycle.")).SequenceEqual(["cycle.a", "cycle.b"])));
        });
        check("protected cyclic members may swap but are never trial-excluded", () =>
        {
            var game = new Session(catalogue) { Report = _ => Error("cycle.a") };
            var client = new Client(); var result = Run(game, client, retained: ["cycle.a", "cycle.b"]);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && game.Tested.Count == 2 && client.Calls == 0
                && result.TrialExclusions.Count == 0 && result.OriginalRestored);
        });
        check("true mutual dependencies permit testing but exclusions keep their dependency closure intact", () =>
        {
            var mods = new ModCatalogue([Mod("cycle.a", ["cycle.b"]), Mod("cycle.b", ["cycle.a"]), Mod("other.mod")]);
            var game = new Session(mods) { Report = ids => ids.Contains("cycle.a") ? Error("cycle.a") : new(true, []) };
            var client = new Client(); var result = Run(game, client, mods: mods, rules: []);
            Assert(result.Outcome == MaintenanceOutcome.Passed && game.Tested.Count == 3 && client.Calls == 0
                && result.TrialExclusions.Single().RemovedIds.ToHashSet().SetEquals(["cycle.a", "cycle.b"])
                && game.Tested[2].SequenceEqual(["other.mod"]));
        });
        foreach (var maximum in new[] { 1, 2 })
            check("cycle experiments honor test limit " + maximum, () =>
            {
                var game = new Session(catalogue) { Report = _ => Error("cycle.a") }; var client = new Client();
                var result = Run(game, client, limits: new(MaximumAttempts: maximum));
                Assert(result.Outcome == MaintenanceOutcome.Stopped && game.Tested.Count == maximum
                    && result.TrialExclusions.Count == 0 && client.Calls == 0 && result.OriginalRestored);
            });
        check("cancelled swap is terminal while interrupted saved feedback resumes without repeating the swap or calling AI", () =>
        {
            var root = Root(); using var cancel = new CancellationTokenSource();
            var game = new Session(catalogue);
            game.Report = _ => { if (game.Tested.Count == 2) cancel.Cancel(); return Error("cycle.a"); };
            var client = new Client(); var first = Run(game, client, root, token: cancel.Token);
            Assert(first.Outcome == MaintenanceOutcome.Cancelled && first.OriginalRestored && game.Tested.Count == 2 && client.Calls == 0);
            AiTaskCheckpoint interrupted;
            using (var task = new AiTaskRecoveryStore(root).Open(first.TaskId!))
            {
                Assert(task.Current.Finished && task.Current.Phase == AiTaskPhase.Cancelled);
                interrupted = task.Current with { Phase = AiTaskPhase.TestSaved };
            }
            game = new Session(catalogue); client = new Client();
            var cancelled = Run(game, client, root, first.TaskId);
            Assert(cancelled.Outcome == MaintenanceOutcome.RecoveryRequired && game.Tested.Count == 0 && client.Calls == 0);
            var interruptedRoot = Root(); string interruptedId;
            using (var task = new AiTaskRecoveryStore(interruptedRoot).Create(interrupted)) interruptedId = task.Id;
            var resumed = Run(game, client, interruptedRoot, interruptedId);
            Assert(resumed.Outcome == MaintenanceOutcome.Passed && game.Tested.Count == 1 && client.Calls == 0
                && game.Tested[0].SequenceEqual(["cycle.b", "other.mod"])
                && resumed.TrialExclusions.Single().SelectedId == "cycle.a",
                $"{resumed.Outcome}: {resumed.Message}; tests={game.Tested.Count}; calls={client.Calls}; order={string.Join(',', resumed.LastAttemptedOrder)}");
        });
        check("unknown ownership, environment failures and incomplete evidence do not authorize cycle experiments", () =>
        {
            foreach (var report in new[] { Error(""), Error("cycle.a") with { CanDiagnose = false },
                Error("cycle.a") with { StartupFailure = new(new("Exited", true, true, [new("GraphicsDriverUnavailable")]), []) } })
            {
                var game = new Session(catalogue) { Report = _ => report }; var client = new Client();
                var result = Run(game, client);
                Assert(game.Tested.Count == 1 && result.TrialExclusions.Count == 0 && !game.Accepted);
            }
        });
        check("swap history ignores unrelated row positions and never recursively permutes", () =>
        {
            string[][] group = [["cycle.a", "cycle.b"]];
            Assert(!OrderingCycleTrials.Swaps(group, ["other.mod", "cycle.b", "cycle.a"], ["cycle.a"],
                [Original, ["cycle.b", "cycle.a", "other.mod"]], default).Any());
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { OrderingCycleTrials.Swaps(group, Original, ["cycle.a"], [Original], cancellation.Token).ToArray(); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("Cancelled experiment continued.");
        });
    }

    private static MaintenanceGameReport Error(string id) => new(false, ["synthetic cycle error"])
        { CanDiagnose = true, DiagnosticFacts = [new("GamePatchFailure", id)] };

    private sealed class Client : IAiClient
    {
        internal int Calls;
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token) => throw new NotSupportedException();
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token)
        { Calls++; return Task.FromResult(new AiClientResult(true, "fixture", new(1, "synthetic", [], []), null) { Usage = new(100, 20, 120) }); }
    }

    private sealed class Session(ModCatalogue catalogue) : ILevelOneGameSession, IMaintenanceTrialSession
    {
        internal readonly List<string[]> Tested = [];
        internal Func<IReadOnlyList<string>, MaintenanceGameReport> Report = _ => new(true, []);
        internal bool Accepted;
        private string[] current = Original.ToArray();
        private string[] allowed = Original.ToArray();
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token)
        { Assert(current.SequenceEqual(order)); Tested.Add(order.ToArray()); return Task.FromResult(Report(order)); }
        public void AuthorizeTrialExclusions(IReadOnlyList<TrialExclusion> trials) =>
            allowed = TrialExclusionPlanning.Replay(catalogue, Original, [], Version, trials);
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token)
        {
            Assert(plan.CanApply && plan.Before.SequenceEqual(current) && plan.After.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(allowed));
            current = plan.After.ToArray(); return Task.CompletedTask;
        }
        public Task FinishAsync(bool accept, CancellationToken token)
        { Accepted = accept; if (!accept) current = Original.ToArray(); return Task.CompletedTask; }
    }
    private static void Assert(bool value, string? message = null) { if (!value) throw new InvalidOperationException(message ?? "Cycle experiment assertion failed."); }
}
