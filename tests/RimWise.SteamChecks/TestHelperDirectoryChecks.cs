using System.Text.Json;
using System.Xml.Linq;
using RimWise.Core;
using RimWise.Integration.Configuration;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Game;

internal static class TestHelperDirectoryChecks
{
    private static void Assert(bool value) { if (!value) throw new Exception("Ordinary helper deployment regression"); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return; }
        throw new Exception("Unsafe deployment was accepted");
    }

    internal static void Run(Action<string, Action> check)
    {
        check("Helper uses ordinary files readable by a fresh directory scan and preserves player configuration", () =>
        {
            var (options, session, path) = Fixture();
            var before = File.ReadAllBytes(options.PersonalConfigPath);
            using (var helper = new TestHelperMount(options, session))
            {
                Assert(new DirectoryInfo(path).LinkTarget is null);
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                    Assert((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0);
                Assert(File.ReadAllBytes(Path.Combine(path, "Assemblies", "RimWise.TestHelper.dll")).Length > 0);
                Assert(!Directory.Exists(Path.Combine(session, "fixed-helper")));
            }
            Assert(!Directory.Exists(path) && Completed(session));
            Assert(before.SequenceEqual(File.ReadAllBytes(options.PersonalConfigPath)));
        });
        check("Ordinary helper repeats rounds without accumulating mods or stale identities", () =>
        {
            var (options, session, path) = Fixture();
            for (var i = 0; i < 3; i++)
            {
                using (var helper = new TestHelperMount(options, session)) Assert(Directory.Exists(path));
                Assert(!Directory.Exists(path) && Completed(session));
            }
        });
        check("Startup recovers an interrupted ordinary helper without deleting player mods or logs", () =>
        {
            var (options, session, path) = Fixture();
            _ = new TestHelperMount(options, session);
            var sentinel = Path.Combine(options.GameDirectory, "Mods", "player.txt");
            var evidence = Path.Combine(session, "evidence.txt");
            File.WriteAllText(sentinel, "player"); File.WriteAllText(evidence, "log");
            var profile = Directory.GetParent(Directory.GetParent(options.TestRoot)!.FullName)!.FullName;
            new PathSettingsStore(profile).Save(new() { GameDirectory = options.GameDirectory });
            TestDataRetention.MaintainProfile(profile);
            Assert(!Directory.Exists(path) && Completed(session));
            Assert(File.ReadAllText(sentinel) == "player" && File.ReadAllText(evidence) == "log");
        });
        check("Ordinary helper cleanup respects the live launcher lease", () =>
        {
            var (options, session, path) = Fixture();
            using var helper = new TestHelperMount(options, session);
            using var lease = File.Open(Path.Combine(options.TestRoot, "ordinary-game-test.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var profile = Directory.GetParent(Directory.GetParent(options.TestRoot)!.FullName)!.FullName;
            new PathSettingsStore(profile).Save(new() { GameDirectory = options.GameDirectory });
            Assert(TestDataRetention.MaintainProfile(profile).Busy && Directory.Exists(path));
        });
        check("Locked ordinary payload preserves both files and cleanup retries", () =>
        {
            var (options, session, path) = Fixture();
            var helper = new TestHelperMount(options, session);
            using (var locked = File.Open(Path.Combine(path, "Assemblies", "RimWise.TestHelper.dll"),
                FileMode.Open, FileAccess.Read, FileShare.None))
            {
                helper.Dispose();
                Assert(!Completed(session) && File.Exists(Path.Combine(path, "About", "About.xml")));
                Reject(() => _ = new TestHelperMount(options, session));
            }
            helper.Dispose(); Assert(!Directory.Exists(path) && Completed(session));
        });
        check("Changed ordinary payload is preserved entirely and never overwritten on another round", () =>
        {
            var (options, session, path) = Fixture();
            var helper = new TestHelperMount(options, session);
            var about = Path.Combine(path, "About", "About.xml");
            var before = File.ReadAllBytes(about);
            File.AppendAllText(about, "changed"); helper.Dispose();
            Assert(!Completed(session) && File.Exists(Path.Combine(path, "Assemblies", "RimWise.TestHelper.dll")));
            Reject(() => _ = new TestHelperMount(options, session));
            File.WriteAllBytes(about, before); helper.Dispose(); Assert(Completed(session));
        });
        check("Unknown ordinary payload is preserved without partially deleting known files", () =>
        {
            var (options, session, path) = Fixture();
            var helper = new TestHelperMount(options, session);
            var unknown = Path.Combine(path, "About", "player.txt"); File.WriteAllText(unknown, "keep");
            helper.Dispose();
            Assert(!Completed(session) && File.ReadAllText(unknown) == "keep"
                && File.Exists(Path.Combine(path, "About", "About.xml")));
            File.Delete(unknown); helper.Dispose(); Assert(!Directory.Exists(path));
        });
        check("Renamed original and same-name replacement directory are both preserved", () =>
        {
            var (options, session, path) = Fixture();
            var helper = new TestHelperMount(options, session);
            var moved = Path.Combine(session, "preserved"); Directory.Move(path, moved);
            Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "player.txt"), "keep");
            helper.Dispose();
            Assert(!Completed(session) && File.ReadAllText(Path.Combine(path, "player.txt")) == "keep"
                && File.Exists(Path.Combine(moved, "About", "About.xml")));
            File.Delete(Path.Combine(path, "player.txt")); Directory.Delete(path); Directory.Move(moved, path);
            helper.Dispose(); Assert(Completed(session));
        });
        check("A nested junction cannot authorize reading or deleting foreign files", () =>
        {
            var (options, session, path) = Fixture();
            var helper = new TestHelperMount(options, session);
            var about = Path.Combine(path, "About"); var preserved = Path.Combine(session, "about");
            Directory.Move(about, preserved);
            TestHelperCleanupChecks.CreateLegacyJunction(about, preserved);
            var before = File.ReadAllBytes(Path.Combine(preserved, "About.xml"));
            helper.Dispose();
            Assert(!Completed(session) && before.SequenceEqual(File.ReadAllBytes(Path.Combine(preserved, "About.xml"))));
            Directory.Delete(about); Directory.Move(preserved, about); helper.Dispose(); Assert(Completed(session));
        });
        check("A foreign root junction replacing the ordinary deployment remains untouched", () =>
        {
            var (options, session, path) = Fixture();
            var helper = new TestHelperMount(options, session);
            var preserved = Path.Combine(session, "preserved"); Directory.Move(path, preserved);
            TestHelperCleanupChecks.CreateLegacyJunction(path, preserved);
            helper.Dispose(); Assert(!Completed(session) && new DirectoryInfo(path).LinkTarget is not null);
            Directory.Delete(path); Directory.Move(preserved, path); helper.Dispose(); Assert(Completed(session));
        });
        check("Missing partial deployment files remain recoverable", () =>
        {
            var (options, session, path) = Fixture();
            var helper = new TestHelperMount(options, session);
            File.Delete(Path.Combine(path, "About", "About.xml"));
            helper.Dispose(); Assert(!Directory.Exists(path) && Completed(session));
        });
        check("Malformed deployment journal preserves one session without blocking valid recovery", () =>
        {
            var (options, session, path) = Fixture();
            _ = new TestHelperMount(options, session);
            var preserved = Path.Combine(session, "preserved"); Directory.Move(path, preserved);
            var next = Path.Combine(options.TestRoot, "game-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(next);
            _ = new TestHelperMount(options, next);
            Directory.Move(preserved, path);
            File.WriteAllText(Path.Combine(session, "helper-deployment.json"), "{}");
            TestHelperMount.CleanInterrupted(options);
            Assert(Directory.Exists(path) && !Completed(session));
            Assert(!Directory.Exists(Path.Combine(options.GameDirectory, "Mods", "RimWise-Test-" + Path.GetFileName(next)[5..])) && Completed(next));
            Reject(() => _ = new TestHelperMount(options, next));
        });
        check("Another installation cannot delete an ordinary helper", () =>
        {
            var (options, session, path) = Fixture();
            using var helper = new TestHelperMount(options, session);
            var foreign = Path.Combine(session, "foreign"); Directory.CreateDirectory(Path.Combine(foreign, "Mods"));
            TestHelperMount.CleanInterrupted(options with { GameDirectory = foreign });
            Assert(Directory.Exists(path));
        });
        check("Unowned same-name directory prevents launch without overwriting or deleting it", () =>
        {
            var (options, session, path) = Fixture();
            Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "player.txt"), "keep");
            Reject(() => _ = new TestHelperMount(options, session));
            Assert(File.ReadAllText(Path.Combine(path, "player.txt")) == "keep");
        });
        check("Hard-linked payload never authorizes deleting a file shared with a player directory", () =>
        {
            var (options, session, path) = Fixture();
            var helper = new TestHelperMount(options, session);
            var about = Path.Combine(path, "About", "About.xml");
            var external = Path.Combine(session, "player.xml");
            File.Copy(about, external); File.Delete(about); JunctionFixture.HardLink(about, external);
            var before = File.ReadAllBytes(external); helper.Dispose();
            Assert(!Completed(session) && before.SequenceEqual(File.ReadAllBytes(external)) && File.Exists(about));
            File.Delete(about); File.Copy(external, about); helper.Dispose(); Assert(Completed(session));
        });
        check("Helper activation and launch arguments remain confined to the independent test configuration", () =>
        {
            var (options, session, _) = Fixture();
            var before = File.ReadAllBytes(options.PersonalConfigPath);
            using var helper = new TestHelperMount(options, session);
            var attempt = Path.Combine(session, "attempt-01"); Directory.CreateDirectory(attempt);
            GameTestLaunch.WriteConfiguration(attempt, "1.7", ["ludeon.rimworld", "sample.mod", GameProbe.PackageId], []);
            var xml = System.Xml.Linq.XDocument.Load(Path.Combine(attempt, "Config", "ModsConfig.xml"));
            Assert(xml.Descendants("activeMods").Elements("li").Select(e => e.Value)
                .SequenceEqual(new[] { "ludeon.rimworld", "sample.mod", GameProbe.PackageId }));
            Assert(GameTestLaunch.Arguments(attempt).Contains("-savedatafolder=" + attempt));
            Assert(before.SequenceEqual(File.ReadAllBytes(options.PersonalConfigPath)));
        });
        check("A copied helper in a different directory cannot pass the original directory identity check", () =>
        {
            var (options, session, path) = Fixture();
            var helper = new TestHelperMount(options, session);
            var moved = Path.Combine(session, "preserved"); Directory.Move(path, moved);
            Directory.CreateDirectory(Path.Combine(path, "About")); Directory.CreateDirectory(Path.Combine(path, "Assemblies"));
            File.Copy(Path.Combine(moved, "About", "About.xml"), Path.Combine(path, "About", "About.xml"));
            File.Copy(Path.Combine(moved, "Assemblies", "RimWise.TestHelper.dll"), Path.Combine(path, "Assemblies", "RimWise.TestHelper.dll"));
            helper.Dispose();
            Assert(!Completed(session) && File.Exists(Path.Combine(path, "About", "About.xml"))
                && File.Exists(Path.Combine(moved, "About", "About.xml")));
        });
        check("Invalid session names cannot authorize helper deployment", () =>
        {
            var (options, session, _) = Fixture();
            var invalid = Path.Combine(options.TestRoot, "game-not-a-guid"); Directory.CreateDirectory(invalid);
            Reject(() => _ = new TestHelperMount(options, invalid));
            Assert(!Directory.EnumerateFileSystemEntries(Path.Combine(options.GameDirectory, "Mods")).Any());
        });
    }

    private static bool Completed(string session)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "helper-cleanup.json")));
        return json.RootElement.GetProperty("Complete").GetBoolean();
    }
    private static (OrdinaryGameTestOptions Options, string Session, string Path) Fixture()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "ordinary-helper-fixtures", Guid.NewGuid().ToString("N"), "中文 空格");
        var game = Path.Combine(root, "game"); Directory.CreateDirectory(Path.Combine(game, "Mods"));
        File.WriteAllText(Path.Combine(game, "RimWorldWin64.exe"), "");
        var config = Path.Combine(root, "ModsConfig.xml"); File.WriteAllText(config, "player configuration sentinel");
        var options = new OrdinaryGameTestOptions(game, config, Path.Combine(root, "profile", "Data", "GameTests"), new GameVersion(1, 7));
        var id = Guid.NewGuid().ToString("N"); var session = Path.Combine(options.TestRoot, "game-" + id);
        Directory.CreateDirectory(session);
        return (options, session, Path.Combine(game, "Mods", "RimWise-Test-" + id));
    }
}
