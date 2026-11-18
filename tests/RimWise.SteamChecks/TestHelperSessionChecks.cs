using System.Xml.Linq;
using RimWise.Core;
using RimWise.Integration.Game;

internal static class TestHelperSessionChecks
{
    private static void Assert(bool value) { if (!value) throw new Exception("Helper session lifecycle regression"); }
    internal static void Run(Action<string, Action> check)
    {
        foreach (var mode in new[] { "clean", "cancel", "launch-failed" })
            check("Ordinary helper session preserves config and cleans process/payload: " + mode,
                () => RunAsync(mode).GetAwaiter().GetResult());
    }

    private static async Task RunAsync(string mode)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "helper-session-fixtures", Guid.NewGuid().ToString("N"), "中文 空格");
        var game = Path.Combine(root, "game"); Directory.CreateDirectory(Path.Combine(game, "Mods"));
        // Only a reviewed synthetic runner, never the historical suspended runner or game binaries.
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            if (Path.GetExtension(file) is ".dll" or ".json") File.Copy(file, Path.Combine(game, Path.GetFileName(file)));
        var executable = Path.Combine(game, "RimWorldWin64.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "RimWise.SteamChecks.exe"), executable);
        File.WriteAllText(Path.Combine(game, "helper-session-fixture.txt"), mode);
        if (mode == "launch-failed") File.WriteAllText(executable, "Not an executable; synthetic launch failure only.");
        var personal = Path.Combine(root, "personal", "ModsConfig.xml"); Directory.CreateDirectory(Path.GetDirectoryName(personal)!);
        File.WriteAllText(personal, "<ModsConfigData><version>1.7</version><activeMods><li>ludeon.rimworld</li></activeMods></ModsConfigData>");
        var before = File.ReadAllBytes(personal);
        var core = Path.Combine(game, "Data", "Core"); Directory.CreateDirectory(core);
        var catalogue = new ModCatalogue([new(core, "Core", ModSource.Game, "",
            new("Core", "Fixture", "ludeon.rimworld", ["1.7"], "", ModRequirements.Empty, false), [])]);
        var options = new OrdinaryGameTestOptions(game, personal, Path.Combine(root, "profile", "Data", "GameTests"),
            new GameVersion(1, 7), 12) { UseFixedHelper = true };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        var progress = new DirectProgress(value =>
        {
            if (mode == "cancel" && value.ProcessId > 0 && value.LogBytes > 0) cancel.Cancel();
        });
        var session = new OrdinaryGameTestSession(options, catalogue, ["ludeon.rimworld"], [], [], progress);
        Assert(await session.VerifySafetyAsync(cancel.Token));
        try
        {
            await session.BeginAsync(cancel.Token);
            try
            {
                var result = await session.TestAsync(["ludeon.rimworld"], cancel.Token);
                Assert(mode == "clean" ? result.StartupVerified : mode == "launch-failed"
                    && session.LastReport?.StopReason == GameTestStopReason.LaunchFailed);
                if (mode == "clean")
                {
                    var second = await session.TestAsync(["ludeon.rimworld"], cancel.Token);
                    Assert(second.StartupVerified);
                }
            }
            catch (OperationCanceledException) when (mode == "cancel" && cancel.IsCancellationRequested) { }
        }
        finally { await session.FinishAsync(false, CancellationToken.None); }
        Assert(!GameProcessService.IsAnyRunning());
        Assert(!Directory.EnumerateFileSystemEntries(Path.Combine(game, "Mods"), "RimWise-Test-*").Any());
        Assert(before.SequenceEqual(File.ReadAllBytes(personal)) && File.Exists(Path.Combine(session.SessionDirectory!, "finished.json")));
        if (mode == "cancel") Assert(session.LastReport?.StopReason == GameTestStopReason.Cancelled);
        using var lease = File.Open(Path.Combine(options.TestRoot, "ordinary-game-test.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    internal static async Task<int> Child(string data)
    {
        var game = AppContext.BaseDirectory;
        var helpers = Directory.GetDirectories(Path.Combine(game, "Mods"), "RimWise-Test-*");
        if (helpers.Length != 1 || new DirectoryInfo(helpers[0]).LinkTarget is not null) return 41;
        var helper = helpers[0];
        if (Directory.EnumerateFileSystemEntries(helper, "*", SearchOption.AllDirectories)
            .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)) return 42;
        if (!XDocument.Load(Path.Combine(data, "Config", "ModsConfig.xml")).Descendants("li").Any(e => e.Value == GameProbe.PackageId)) return 43;
        _ = File.ReadAllBytes(Path.Combine(helper, "Assemblies", "RimWise.TestHelper.dll"));
        File.WriteAllText(Path.Combine(data, "Player.log"), "INFO ordinary helper read by fresh synthetic process\n");
        if (File.ReadAllText(Path.Combine(game, "helper-session-fixture.txt")) == "cancel")
        { await Task.Delay(30000); return 0; }
        new XElement("signal", new XAttribute("nonce", GameProbe.ReadNonce(data)!), new XAttribute("pid", Environment.ProcessId),
            new XAttribute("stage", "map-ready"), new XAttribute("ticks", 120), new XAttribute("colonists", 1),
            new XAttribute("errors", 0), new XAttribute("menu", 1), new XAttribute("factions", 1), new XAttribute("pawns", 1))
            .Save(Path.Combine(data, "workbench-signal.xml"));
        return 0;
    }
    private sealed class DirectProgress(Action<OrdinaryGameProgress> report) : IProgress<OrdinaryGameProgress>
    { public void Report(OrdinaryGameProgress value) => report(value); }
}
