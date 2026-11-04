using RimWise.Core;
using RimWise.Integration.Game;
using RimWise.Integration.Configuration;
using RimWise.Integration.Diagnostics;
using System.Text.Json;

internal static class TestHelperCleanupChecks
{
    private static void Assert(bool condition) { if (!condition) throw new Exception("Test helper cleanup regression"); }

    internal static void Run(Action<string, Action> check)
    {
        check("Owned helper junction is removed after use", () =>
        {
            var (options, session) = Fixture();
            var mount = new TestHelperMount(options, session);
            var link = Path.Combine(options.GameDirectory, "Mods", "RimWise-Test-" + Path.GetFileName(session)[5..]);
            Assert(new DirectoryInfo(link).LinkTarget is not null);
            mount.Dispose();
            Assert(!Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is null);
            Assert(!Directory.Exists(Target(session)) && Completed(session));
            mount.Dispose();
            Assert(Completed(session));
        });
        check("Each test round can remount and clean the same session", () =>
        {
            var (options, session) = Fixture();
            for (var i = 0; i < 3; i++)
            {
                using (var mount = new TestHelperMount(options, session)) Assert(Directory.Exists(Link(options, session)));
                Assert(!Directory.Exists(Link(options, session)) && !Directory.Exists(Target(session)) && Completed(session));
            }
        });
        check("Interrupted session cleanup removes helper payload and preserves diagnosis", () =>
        {
            var (options, session) = Fixture();
            _ = new TestHelperMount(options, session); // Simulates parent exit without Dispose.
            var log = Path.Combine(session, "Player.log");
            File.WriteAllText(log, "diagnostic evidence");
            TestHelperMount.CleanInterrupted(options);
            Assert(!Directory.Exists(Link(options, session)) && !Directory.Exists(Target(session))
                && File.ReadAllText(log) == "diagnostic evidence" && Completed(session));
        });
        check("Legacy helper remnants are adopted only with matching installation and metadata", () =>
        {
            var (options, session) = Fixture();
            _ = new TestHelperMount(options, session);
            File.Delete(Path.Combine(session, "helper-mount.json"));
            TestHelperMount.CleanInterrupted(options);
            Assert(!Directory.Exists(Link(options, session)) && !Directory.Exists(Target(session)) && Completed(session));
        });
        check("Dangling owned junction is detached without following its missing target", () =>
        {
            var (options, session) = Fixture();
            _ = new TestHelperMount(options, session);
            var moved = Path.Combine(session, "preserved-payload");
            Directory.Move(Target(session), moved);
            TestHelperMount.CleanInterrupted(options);
            Assert(new DirectoryInfo(Link(options, session)).LinkTarget is null && Directory.Exists(moved) && Completed(session));
        });
        check("Locked helper payload records pending cleanup and retries after release", () =>
        {
            var (options, session) = Fixture();
            var mount = new TestHelperMount(options, session);
            using (var locked = File.Open(Path.Combine(Target(session), "Assemblies", "RimWise.TestHelper.dll"),
                FileMode.Open, FileAccess.Read, FileShare.None))
            {
                mount.Dispose();
                Assert(!Directory.Exists(Link(options, session)) && Directory.Exists(Target(session)) && !Completed(session));
            }
            TestHelperMount.CleanInterrupted(options);
            Assert(!Directory.Exists(Target(session)) && Completed(session));
        });
        check("Changed helper file is preserved until its original content is restored", () =>
        {
            var (options, session) = Fixture();
            var mount = new TestHelperMount(options, session);
            var file = Path.Combine(Target(session), "About", "About.xml");
            var before = File.ReadAllBytes(file);
            File.AppendAllText(file, "user change");
            mount.Dispose();
            Assert(File.Exists(file) && File.Exists(Path.Combine(Target(session), "Assemblies", "RimWise.TestHelper.dll"))
                && !Completed(session));
            File.WriteAllBytes(file, before);
            TestHelperMount.CleanInterrupted(options);
            Assert(!Directory.Exists(Target(session)) && Completed(session));
        });
        check("Unregistered helper files are preserved and empty-tree cleanup remains retryable", () =>
        {
            var (options, session) = Fixture();
            var mount = new TestHelperMount(options, session);
            var file = Path.Combine(Target(session), "personal.txt");
            File.WriteAllText(file, "preserve");
            mount.Dispose();
            Assert(File.ReadAllText(file) == "preserve" && !Completed(session));
            File.Delete(file);
            TestHelperMount.CleanInterrupted(options);
            Assert(!Directory.Exists(Target(session)) && Completed(session));
        });
        check("An ordinary player directory replacing the link is never deleted", () =>
        {
            var (options, session) = Fixture();
            var mount = new TestHelperMount(options, session);
            var link = Link(options, session);
            Directory.Delete(link, recursive: false);
            Directory.CreateDirectory(link);
            File.WriteAllText(Path.Combine(link, "personal.txt"), "preserve");
            mount.Dispose();
            Assert(File.ReadAllText(Path.Combine(link, "personal.txt")) == "preserve" && !Completed(session));
        });
        check("A junction replaced by another helper's link preserves both targets", () =>
        {
            var (options, session) = Fixture();
            var (other, otherSession) = Fixture();
            var mount = new TestHelperMount(options, session);
            using var foreign = new TestHelperMount(other, otherSession);
            var link = Link(options, session);
            Directory.Delete(link, recursive: false);
            Directory.Move(Link(other, otherSession), link);
            mount.Dispose();
            Assert(Directory.Exists(Target(session)) && Directory.Exists(Target(otherSession)) && !Completed(session));
            Directory.Move(link, Link(other, otherSession));
            mount.Dispose();
            Assert(!Directory.Exists(Target(session)));
        });
        check("Startup maintenance cleans helper remnants without changing personal configuration", () =>
        {
            var (options, session) = Fixture();
            var profile = Directory.GetParent(Directory.GetParent(options.TestRoot)!.FullName)!.FullName;
            new PathSettingsStore(profile).Save(new() { GameDirectory = options.GameDirectory });
            var before = File.ReadAllBytes(options.PersonalConfigPath);
            _ = new TestHelperMount(options, session);
            TestDataRetention.MaintainProfile(profile);
            Assert(!Directory.Exists(Link(options, session)) && !Directory.Exists(Target(session)) && Completed(session)
                && before.SequenceEqual(File.ReadAllBytes(options.PersonalConfigPath)));
        });
        check("Startup cleanup respects a live launch lease even before a game process exists", () =>
        {
            var (options, session) = Fixture();
            var profile = Directory.GetParent(Directory.GetParent(options.TestRoot)!.FullName)!.FullName;
            new PathSettingsStore(profile).Save(new() { GameDirectory = options.GameDirectory });
            using var mount = new TestHelperMount(options, session);
            using var lease = File.Open(Path.Combine(options.TestRoot, "ordinary-game-test.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            Assert(TestDataRetention.MaintainProfile(profile).Busy && Directory.Exists(Link(options, session)));
        });
        check("Another installation's helper is preserved", () =>
        {
            var (options, session) = Fixture();
            using var mount = new TestHelperMount(options, session);
            var foreignGame = Path.Combine(Path.GetDirectoryName(options.GameDirectory)!, "foreign-game");
            Directory.CreateDirectory(Path.Combine(foreignGame, "Mods"));
            TestHelperMount.CleanInterrupted(options with { GameDirectory = foreignGame });
            Assert(Directory.Exists(Link(options, session)) && Directory.Exists(Target(session)));
        });
        check("A malformed helper journal cannot authorize deletion or block another session cleanup", () =>
        {
            var (options, session) = Fixture();
            using var mount = new TestHelperMount(options, session);
            var otherSession = Path.Combine(options.TestRoot, "game-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(otherSession);
            _ = new TestHelperMount(options, otherSession);
            File.WriteAllText(Path.Combine(session, "helper-mount.json"), "{}");
            TestHelperMount.CleanInterrupted(options);
            Assert(Directory.Exists(Link(options, session)) && Directory.Exists(Target(session))
                && !Directory.Exists(Link(options, otherSession)) && !Directory.Exists(Target(otherSession)));
        });
        check("Malformed legacy session metadata preserves files without aborting later cleanup", () =>
        {
            var (options, session) = Fixture();
            _ = new TestHelperMount(options, session);
            File.Delete(Path.Combine(session, "helper-mount.json"));
            File.WriteAllText(Path.Combine(session, "session.json"), "{\"Schema\":\"invalid\"}");
            var next = Path.Combine(options.TestRoot, "game-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(next);
            _ = new TestHelperMount(options, next);
            TestHelperMount.CleanInterrupted(options);
            Assert(Directory.Exists(Link(options, session)) && Directory.Exists(Target(session))
                && !Directory.Exists(Link(options, next)) && !Directory.Exists(Target(next)));
        });
    }

    private static string Link(OrdinaryGameTestOptions options, string session) =>
        Path.Combine(options.GameDirectory, "Mods", "RimWise-Test-" + Path.GetFileName(session)[5..]);
    private static string Target(string session) => Path.Combine(session, "fixed-helper");
    private static bool Completed(string session)
    {
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "helper-cleanup.json")));
        return state.RootElement.GetProperty("Complete").GetBoolean();
    }

    private static (OrdinaryGameTestOptions Options, string Session) Fixture()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "helper-cleanup-fixtures", Guid.NewGuid().ToString("N"), "中文 空格");
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(game, "Mods"));
        File.WriteAllText(Path.Combine(game, "RimWorldWin64.exe"), "");
        var config = Path.Combine(root, "ModsConfig.xml");
        File.WriteAllText(config, "<ModsConfigData><activeMods /></ModsConfigData>");
        var options = new OrdinaryGameTestOptions(game, config, Path.Combine(root, "profile", "Data", "GameTests"), new GameVersion(1, 7))
            { UseFixedHelper = true };
        var session = Path.Combine(options.TestRoot, "game-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, "session.json"), JsonSerializer.Serialize(new
            { Schema = 1, Executable = Path.Combine(game, "RimWorldWin64.exe"), OriginalConfigHash = "fixture" }));
        return (options, session);
    }
}
