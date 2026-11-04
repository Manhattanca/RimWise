using RimWise.Core;
using RimWise.Integration.Databases;

internal static class DiagnosticSummaryChecks
{
    private static readonly GameVersion Version = new(1, 7);
    private static ModInstallation Mod(string id) => new(id, id, ModSource.Local, "",
        new(id, "", id, ["1.7"], "", ModRequirements.Empty, false), []);
    private static DatabaseSnapshot Database(params CommunityRule[] rules) => DatabaseParser.Create(
        new(1, new('a', 40), new('b', 64), new('c', 40), new('d', 64), rules, []));
    private static void Assert(bool value) { if (!value) throw new Exception("Diagnostic summary regression"); }

    internal static void Run(Action<string, Action> check)
    {
        check("A misplaced top rule with 216 peers is one warning, not 432 row warnings", () =>
        {
            var order = Enumerable.Range(0, 216).Select(i => "peer." + i).Append("first").ToArray();
            var catalogue = new ModCatalogue(order.Select(Mod).ToArray());
            var db = Database(new CommunityRule("first", [], [], [], true, false));
            var issues = db.Inspect(catalogue, order, Version, []);
            Assert(issues.Count == 1 && issues[0].Code == "CommunityLoadTopViolated"
                && issues[0].AffectedIdentifiers.Count() == 217);
            Assert(db.Rules(catalogue, order, Version, []).Count == 216);
            Assert(db.Inspect(catalogue, ["first", ..order.Take(216)], Version, []).Count == 0);
        });
        check("Boundary summaries preserve overrides, aliases, inactive rules and separate explicit violations", () =>
        {
            var catalogue = new ModCatalogue([Mod("a"), Mod("b"), Mod("last"), Mod("unused")]);
            var db = Database(new("last", [], [], [], false, true), new("a", ["b"], [], [], false, false),
                new("unused", [], [], [], true, false));
            var issues = db.Inspect(catalogue, ["last", "b", "a"], Version, [new("last", "b")]);
            Assert(issues.Count == 2 && issues.Single(i => i.Code == "CommunityLoadBottomViolated")
                .RelatedIdentifiers.SequenceEqual(["a"]));
            Assert(issues.All(i => !i.AffectedIdentifiers.Contains("unused")));
            var alias = Mod("a") with { Source = ModSource.Workshop };
            issues = Database(new CommunityRule("a", ["b"], [], [], false, false))
                .Inspect(new([alias, Mod("b")]), ["b", "a_steam"], Version, []);
            Assert(issues.Count == 1 && issues[0].Identifier == "a_steam" && issues[0].RelatedIdentifiers.Contains("b"));
        });
        check("One shared conflict retains both row markers and evidence locations", () =>
        {
            var issue = new CatalogueDiagnostic("DeclaredConflict", DiagnosticSeverity.Error, "A conflicts with B", "a/About.xml", "a");
            var issues = DiagnosticSummary.Consolidate([issue, issue with { Identifier = "b", Location = "b/About.xml" }, issue]);
            Assert(issues.Count == 1 && issues[0].AffectedIdentifiers.SequenceEqual(["a", "b"])
                && issues[0].Location.Contains("a/About.xml") && issues[0].Location.Contains("b/About.xml"));
            Assert(DiagnosticSummary.Consolidate([issue, issue with { Message = "A conflicts with C" }]).Count == 2);
            var local = issue with { Code = "MetadataUnreadable" };
            Assert(DiagnosticSummary.Consolidate([local, local with { Identifier = "b" }]).Count == 2);
        });
        check("Version declarations and actual missing prerequisites remain visible only for enabled mods", () =>
        {
            var old = Mod("old") with { Metadata = Mod("old").Metadata! with { SupportedVersions = ["1.6"] } };
            var missing = Mod("missing") with { Metadata = Mod("missing").Metadata! with
                { Requirements = new([new("absent", "Absent")], [], [], []) } };
            var catalogue = new ModCatalogue([old, missing, Mod("good")]);
            var issues = DiagnosticSummary.Consolidate(CatalogueAnalysis.InspectSelectedOrder(catalogue, ["old", "missing"], Version, Version));
            Assert(issues.Count(i => i.Code == "VersionNotDeclared") == 1 && issues.Count(i => i.Code == "DependencyNotInstalled") == 1);
            Assert(CatalogueAnalysis.InspectSelectedOrder(catalogue, ["good"], Version, Version).Count == 0);
        });
    }
}
