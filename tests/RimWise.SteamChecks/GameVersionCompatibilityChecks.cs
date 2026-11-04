using System.Text;
using System.Xml.Linq;
using RimWise.Core;
using RimWise.Formats;

internal static class GameVersionCompatibilityChecks
{
    internal static void Run(Action<string, Action> check)
    {
        check("Four-component game and configuration versions resolve without inventing support", () =>
        {
            foreach (var text in new[] { "1.7.4995.1069", "v1.7.4995.1069", "Version: 1.7.4995.1069 rev1" })
                Assert(GameVersion.Parse(text) == new GameVersion(1, 7, 4995));
            foreach (var text in new[] { "1.7.4995.bad", "1.7.4995.1.2", "1.7.bad", "random 1.7", "1.7.4995malformed" })
                Assert(GameVersion.Parse(text) is null);
        });
        check("Modern series metadata does not block order saves and preserves target configuration fields", () =>
        {
            var store = Store("modern");
            File.WriteAllText(store.TargetPath, Xml("1.6.4633", "<custom><value>中文保留</value></custom><knownExpansions><li>official</li></knownExpansions>"));
            var initial = File.ReadAllBytes(store.TargetPath);
            var plan = store.Prepare(Document("1.7.4995.1069", ["TEST.MOD", "LUDEON.RIMWORLD"]), new GameVersion(1, 7, 4995));
            Assert(File.ReadAllBytes(store.TargetPath).SequenceEqual(initial) && plan.After.GameVersion == "1.7.4995");
            var commit = plan.Apply();
            var saved = XDocument.Load(store.TargetPath);
            Assert(commit.Changed && File.ReadAllBytes(commit.BackupPath).SequenceEqual(initial));
            Assert(saved.Root!.Element("version")!.Value == "1.7.4995"
                && saved.Root.Element("custom")!.Element("value")!.Value == "中文保留"
                && saved.Root.Element("knownExpansions")!.Element("li")!.Value == "official"
                && saved.Root.Element("activeMods")!.Elements("li").Select(element => element.Value).SequenceEqual(["test.mod", "ludeon.rimworld"]));
            var savedBytes = File.ReadAllBytes(store.TargetPath);
            commit.Undo(); Assert(File.ReadAllBytes(store.TargetPath).SequenceEqual(initial));
            commit.Redo(); Assert(File.ReadAllBytes(store.TargetPath).SequenceEqual(savedBytes));
        });
        check("Unchanged order still updates a stale modern version marker with backup", () =>
        {
            var store = Store("version-only");
            File.WriteAllText(store.TargetPath, Xml("1.6"));
            var plan = store.Prepare(Document("1.6"), new GameVersion(1, 7));
            var commit = plan.Apply();
            Assert(commit.Changed && File.Exists(commit.BackupPath) && XDocument.Load(store.TargetPath).Root!.Element("version")!.Value == "1.7");
        });
        check("Missing or unrecognized version can use a known modern runtime without losing XML", () =>
        {
            foreach (var version in new[] { "", "unrecognized" })
            {
                var store = Store("unknown");
                File.WriteAllText(store.TargetPath, Xml(version));
                store.Prepare(Document(version), new GameVersion(1, 7)).Apply();
                Assert(XDocument.Load(store.TargetPath).Root!.Element("version")!.Value == "1.7");
            }
            var noVersion = Store("missing");
            File.WriteAllText(noVersion.TargetPath, Xml("1.6").Replace("<version>1.6</version>", ""));
            noVersion.Prepare(Document("1.7"), new GameVersion(1, 7)).Apply();
            Assert(XDocument.Load(noVersion.TargetPath).Root!.Element("version")!.Value == "1.7");
        });
        check("Same-series four-component markers stay byte-identical on unchanged saves", () =>
        {
            var store = Store("same");
            File.WriteAllText(store.TargetPath, Xml("1.7.4995.1069"));
            var initial = File.ReadAllBytes(store.TargetPath);
            var commit = store.Prepare(Document("1.7.4995.1069"), new GameVersion(1, 7, 4995)).Apply();
            Assert(!commit.Changed && !Directory.Exists(store.BackupDirectory) && File.ReadAllBytes(store.TargetPath).SequenceEqual(initial));
        });
        check("Automatic final save retains target version and rejects externally changed configuration", () =>
        {
            var store = Store("automatic");
            File.WriteAllText(store.TargetPath, Xml("1.6"));
            var baseline = store.Prepare(Document("1.6"), new GameVersion(1, 7));
            var final = baseline.WithOrder(new LoadOrder(["TEST.MOD", "LUDEON.RIMWORLD"]));
            Assert(final.After.GameVersion == "1.7" && final.After.Order.PackageIds.SequenceEqual(["test.mod", "ludeon.rimworld"]));
            File.AppendAllText(store.TargetPath, "\n<!-- external -->");
            var external = File.ReadAllBytes(store.TargetPath);
            Reject(() => final.Apply());
            Reject(() => baseline.WithOrder(new LoadOrder(["test.mod"])));
            Assert(File.ReadAllBytes(store.TargetPath).SequenceEqual(external) && !Directory.Exists(store.BackupDirectory));
        });
        check("Legacy folder identifier migrations remain blocked while same legacy series preserves spelling", () =>
        {
            foreach (var (existing, proposed, target) in new[] { ("1.0", "1.7", new GameVersion(1, 7)),
                ("1.7", "1.0", new GameVersion(1, 7)), ("1.7", "1.7", new GameVersion(1, 0)),
                ("0.19", "0.18", new GameVersion(0, 19)) })
            {
                var store = Store("legacy-reject");
                File.WriteAllText(store.TargetPath, Xml(existing));
                var original = File.ReadAllBytes(store.TargetPath);
                Reject(() => store.Prepare(Document(proposed), target));
                Assert(File.ReadAllBytes(store.TargetPath).SequenceEqual(original) && !Directory.Exists(store.BackupDirectory));
            }
            var legacy = Store("legacy-same");
            File.WriteAllText(legacy.TargetPath, Xml("1.0"));
            var plan = legacy.Prepare(Document("1.0", ["FolderName", "Core"]), new GameVersion(1, 0));
            Assert(plan.After.Order.PackageIds.SequenceEqual(["FolderName", "Core"]));
        });
        check("Malformed config is not repaired by relaxing version checks", () =>
        {
            var store = Store("malformed");
            File.WriteAllText(store.TargetPath, "<ModsConfigData><version>1.6</version><activeMods/><activeMods/></ModsConfigData>");
            Reject(() => store.Prepare(Document("1.7"), new GameVersion(1, 7)));
            Assert(!Directory.Exists(store.BackupDirectory));
        });
    }

    private static GameOrderStore Store(string name)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "work", "version-compatibility-checks", name + " 中文 [test] & " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        return new GameOrderStore(Path.Combine(root, "Config"), Path.Combine(root, "Backup"));
    }

    private static string Xml(string version, string extra = "") =>
        $"<ModsConfigData><version>{version}</version><activeMods><li>ludeon.rimworld</li><li>test.mod</li></activeMods>{extra}</ModsConfigData>";

    private static ModsConfigDocument Document(string version, string[]? order = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Xml(version)));
        var document = new ModsConfigXmlFormat().Read(stream);
        return order is null ? document : document.WithOrder(new LoadOrder(order));
    }

    private static void Assert(bool value) { if (!value) throw new Exception("Game version compatibility regression"); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { return; }
        throw new Exception("Unsafe configuration accepted");
    }
}
