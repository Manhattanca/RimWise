using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Game;

internal static class CascadingTypeLoadChecks
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "cascading-type-checks", Guid.NewGuid().ToString("N"));
    private static readonly string[] Order = ["ludeon.rimworld", "test.observer", "test.broken", "test.translation"];
    private const string Observer = "Example.Observer.Settings";
    private const string Broken = "Example.Broken.Entry";
    private const string Error = "Error while instantiating a mod of type " + Observer
        + ": System.Reflection.TargetInvocationException: Exception has been thrown by the target of an invocation. ---> System.TypeLoadException: "
        + "Could not load type of field '" + Broken + ":scrollPosition' (2) due to: Could not resolve type with token 0100003b "
        + "from typeref (expected class 'UnityEngine.Vector2' in assembly 'ExampleStubs, Version=1.0.0.0')\n"
        + " at System.RuntimeType.IsAssignableFrom(System.Type c)\n at " + Observer + ".Scan()";
    private static readonly AiSymbolOwner[] Owners = [new(Observer, Order[1], "assembly-type"), new(Broken, Order[2], "assembly-type")];
    private static void Assert(bool value, string message = "Cascading type attribution regression")
    { if (!value) throw new Exception(message); }
    private static ModInstallation Mod(string id, params string[] dependencies) => new(Path.Combine(Root, id), id,
        id == Order[0] ? ModSource.Game : ModSource.Local, "", new(id, "test", id, ["1.7"], "",
            new(dependencies.Select(d => new ModDependency(d, d)).ToArray(), [], [], []), false), []);
    private static ModCatalogue Catalogue() => new([Mod(Order[0]), Mod(Order[1]), Mod(Order[2]), Mod(Order[3], Order[2])]);
    private static GameLogSnapshot Log(string text = Error) => new(Path.Combine(Root, "Player.log"), DateTimeOffset.UtcNow,
        text, [new(text.Split('\n')[0], 1, [Order[1]], text)], false);
    private static MaintenanceGameReport Feedback(GameLogSnapshot log, IReadOnlyList<AiSymbolOwner> owners) => new(false,
        log.Issues.Select(i => i.Summary).ToArray())
    {
        CanDiagnose = true, DiagnosticFacts = GameTestDiagnostics.Project([log], owners),
        LogContext = new([new("startup", log.Text)], owners, false)
    };

    internal static void Run(Action<string, Action> check)
    {
        check("Nested field declaring type takes precedence over the scanning mod and framework typeref", () =>
            Assert(DiagnosticLogContext.SubjectSymbols([Error]).SequenceEqual([Broken])));
        check("Wrapped type failure selects the verified inner owner and its dependent, not the observer", () =>
        {
            var report = Feedback(Log(), Owners);
            Assert(report.DiagnosticFacts.All(f => f.PackageId == Order[2]));
            var trials = TrialExclusionPlanning.Candidates(report, Catalogue(), Order, [], new(1, 7), 1);
            Assert(trials.Count == 1 && trials[0].SelectedId == Order[2]
                && trials[0].RemovedIds.ToHashSet().SetEquals([Order[2], Order[3]]));
            Assert(TrialExclusionPlanning.Replay(Catalogue(), Order, [], new(1, 7), trials).SequenceEqual(Order[..2]));
        });
        check("Missing or XML-only inner ownership cannot fall back to excluding the observer", () =>
        {
            foreach (var owners in new AiSymbolOwner[][] { [Owners[0]], [Owners[0], Owners[1] with { Basis = "xml-symbol" }] })
            {
                var report = Feedback(Log(), owners);
                Assert(report.DiagnosticFacts.Count > 0 && report.DiagnosticFacts.All(f => f.PackageId.Length == 0));
                Assert(TrialExclusionPlanning.Candidates(report, Catalogue(), Order, [], new(1, 7), 1).Count == 0);
            }
        });
        check("Protected inner owners never cause unrelated observer exclusion", () =>
        {
            var report = Feedback(Log(), Owners);
            foreach (var protectedId in new[] { Order[2], Order[3] })
                Assert(TrialExclusionPlanning.Candidates(report, Catalogue(), Order, [protectedId], new(1, 7), 1).Count == 0);
        });
        check("Helper and reset blockers use the inner field owner instead of reflection scan frames", () =>
        {
            foreach (var log in new[] { Log() with { Path = Path.Combine(Root, "workbench-helper-failure.txt") },
                Log() with { ModResetError = Error } })
            {
                Assert(DiagnosticLogContext.BlockingSymbols([log]).SequenceEqual([Broken]));
                var report = Feedback(log, Owners) with { LogContext = new([], Owners, false) { BlockingError = Error } };
                Assert(report.DiagnosticFacts.Where(f => f.Code == "GameTestBlockingFailure").All(f => f.PackageId == Order[2]));
                Assert(TrialExclusionPlanning.Candidates(report, Catalogue(), Order, [], new(1, 7), 1).Single().SelectedId == Order[2]);
                var unknown = Feedback(log, [Owners[0]]) with { LogContext = new([], [Owners[0]], false) { BlockingError = Error } };
                Assert(TrialExclusionPlanning.Candidates(unknown, Catalogue(), Order, [], new(1, 7), 1).Count == 0);
            }
        });
        check("A later recovery error cannot replace the reset exception's original blocking frames", () =>
        {
            var text = "System.NullReferenceException: original blocker\n at Example.Initial.Load()\n" + Error;
            var log = Log(text) with { ModResetError = text };
            Assert(DiagnosticLogContext.BlockingSymbols([log]).First() == "Example.Initial.Load");
        });
        check("Independent errors in the observer remain eligible and warnings remain observations", () =>
        {
            const string independent = "Could not load type '" + Observer + "'";
            var log = Log() with { Issues = [.. Log().Issues, new(independent, 1, [Order[1]], independent)] };
            Assert(TrialExclusionPlanning.Candidates(Feedback(log, Owners), Catalogue(), Order, [], new(1, 7), 1)
                .Any(t => t.SelectedId == Order[1]));
            var warning = Log() with { Issues = [], Warnings = [new(Error, 1, [Order[1]], Error)] };
            Assert(GameTestDiagnostics.Project([warning], Owners).Count == 0
                && GameTestDiagnostics.ProjectWarnings([warning]) is not null);
        });
        check("Own field failures retain self attribution and nested generic type names", () =>
        {
            var own = Feedback(Log(Error.Replace(Broken, Observer)), Owners);
            Assert(own.DiagnosticFacts.Any(f => f.PackageId == Order[1]));
            Assert(DiagnosticLogContext.SubjectSymbols([Error.Replace(Broken, "Example.Broken.Entry`1+Nested")])
                .SequenceEqual(["Example.Broken.Entry`1+Nested"]));
        });
        check("Field subjects from detail reach existing static PE ownership without executing mod code", () =>
        {
            var assembly = typeof(GameTestDiagnostics).Assembly.Location;
            var symbol = typeof(GameTestDiagnostics).FullName!;
            var text = Error.Replace(Broken, symbol);
            var log = Log(text) with { Issues = [new("Error while instantiating observer", 1, [], text)] };
            var catalogue = new ModCatalogue([new ModInstallation(Path.GetDirectoryName(assembly)!, Order[2], ModSource.Local,
                "", Mod(Order[2]).Metadata, [])]);
            var context = DiagnosticLogContext.Create(new(1, 0, GameTestStopReason.Exited, true, true, [log]),
                catalogue, [Order[2]], CancellationToken.None, new(1, 7));
            Assert(context.Owners.Any(o => o.Symbol == symbol && o.PackageId == Order[2] && o.Basis == "assembly-type"));
        });
    }

    internal static void Replay(string sessionDirectory)
    {
        var checkedWrappers = 0; var resets = 0;
        foreach (var folder in Directory.EnumerateDirectories(sessionDirectory, "attempt-*"))
        {
            var path = Path.Combine(folder, "Player.log");
            if (!File.Exists(path)) continue;
            var log = PlayerLogReader.Read(path);
            using var archived = JsonDocument.Parse(File.ReadAllText(Path.Combine(sessionDirectory,
                "attempt-01 测试数据", "diagnostic-context.json")));
            var context = archived.RootElement.GetProperty("Logs").Deserialize<AiLogContext>()!;
            var wrappers = log.Issues.Where(i => i.Summary.Contains("Error while instantiating a mod of type", StringComparison.Ordinal)
                && i.Detail.Contains("Could not load type of field '", StringComparison.Ordinal)).ToArray();
            foreach (var issue in wrappers)
            {
                var subjects = DiagnosticLogContext.SubjectSymbols([issue.Detail]);
                Assert(subjects.Length > 0);
                var facts = GameTestDiagnostics.Project([log with { Issues = [issue], ModResetError = null }], context.Owners);
                Assert(facts.All(f => f.PackageId.Length == 0 || context.Owners.Any(o => o.PackageId == f.PackageId
                    && o.Basis == "assembly-type" && subjects.Contains(o.Symbol))));
                checkedWrappers++;
            }
            if (log.ModResetError is not null)
            {
                resets++;
                var symbols = DiagnosticLogContext.BlockingSymbols([log]);
                Assert(symbols.Length > 0);
                var facts = GameTestDiagnostics.Project([log], context.Owners);
                var report = Feedback(log, context.Owners) with { DiagnosticFacts = facts,
                    LogContext = context with { BlockingError = log.ModResetError } };
                // This archive lacks a verified owner of the reset exception. Preserve 0.5.5's refusal
                // to use unrelated startup candidates; never invent a missing installation mapping.
                Assert(TrialExclusionPlanning.Candidates(report, Catalogue(), Order, [], new(1, 7), 1).Count == 0);
            }
        }
        Assert(checkedWrappers > 0 && resets > 0);
        Console.WriteLine(JsonSerializer.Serialize(new { ArchivedWrappers = checkedWrappers, ResetAttempts = resets,
            RealModRetest = false, UsedArchivedVerifiedOwners = true }));
    }
}
