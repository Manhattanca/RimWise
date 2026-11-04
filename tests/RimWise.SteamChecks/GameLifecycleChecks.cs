using System.Diagnostics;
using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.Game;

internal static class GameLifecycleChecks
{
    private static void Assert(bool value) { if (!value) throw new Exception("Game lifecycle regression"); }
    internal static void Run(Action<string, Action> check)
    {
        check("Retained handle to an exited process is not a running game", () =>
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RimWise.SteamChecks.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("--lifecycle-probe");
            using var process = Process.Start(start)!;
            Assert(process.StandardOutput.ReadLine() == "ready" && GameProcessService.IsRunning(process));
            process.StandardInput.WriteLine("exit");
            Assert(process.WaitForExit(10000) && !GameProcessService.IsRunning(process));
        });
        check("Foreign installation history is preserved and cannot block the current installation", () =>
        {
            var fixture = Fixture();
            var attempt = WriteSession(fixture, Path.Combine(fixture.TestRoot, "other", "RimWorldWin64.exe"), 1);
            var before = File.ReadAllBytes(Path.Combine(attempt, "launch.json"));
            var result = GameTestRecovery.StopInterruptedAsync(fixture, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Count == 1 && !result[0].ProcessFound && !File.Exists(Path.Combine(attempt, "recovered.json"))
                && File.ReadAllBytes(Path.Combine(attempt, "launch.json")).SequenceEqual(before));
        });
        check("More than twenty dead attempts recover after personal order changes without writing it", () =>
        {
            var fixture = Fixture();
            WriteSession(fixture, GameProcessService.FindExecutable(fixture.GameDirectory), 21);
            var before = File.ReadAllBytes(fixture.PersonalConfigPath);
            var result = GameTestRecovery.StopInterruptedAsync(fixture, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Count == 21 && result.All(r => !r.ProcessFound)
                && File.ReadAllBytes(fixture.PersonalConfigPath).SequenceEqual(before));
            Assert(GameTestRecovery.Inspect(fixture).Count == 0);
        });
        check("A malformed matching-installation launch journal still cannot authorize recovery", () =>
        {
            var fixture = Fixture();
            var attempt = WriteSession(fixture, GameProcessService.FindExecutable(fixture.GameDirectory), 1);
            File.WriteAllText(Path.Combine(attempt, "launch.json"), "{}");
            try { GameTestRecovery.StopInterruptedAsync(fixture, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { Assert(!File.Exists(Path.Combine(attempt, "recovered.json"))); return; }
            throw new Exception("Invalid journal accepted");
        });
    }

    private static OrdinaryGameTestOptions Fixture()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "lifecycle-fixtures", Guid.NewGuid().ToString("N"), "中文 空格");
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);
        // Empty path marker only; never a copied or executable game/runner assembly.
        File.WriteAllText(Path.Combine(game, "RimWorldWin64.exe"), "");
        var config = Path.Combine(root, "ModsConfig.xml");
        File.WriteAllText(config, "<ModsConfigData><activeMods /></ModsConfigData>");
        return new(game, config, Path.Combine(root, "tests"), new GameVersion(1, 7));
    }

    private static string WriteSession(OrdinaryGameTestOptions options, string executable, int count)
    {
        var session = Path.Combine(options.TestRoot, "game-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, "session.json"), JsonSerializer.Serialize(new
            { Schema = 1, Executable = executable, OriginalConfigHash = "previous-configuration" }));
        var since = DateTime.UtcNow.AddSeconds(-1);
        var attempt = "";
        for (var i = 0; i < count; i++)
        {
            attempt = Path.Combine(session, $"attempt-{i + 1:00} 测试数据");
            Directory.CreateDirectory(attempt);
            File.WriteAllText(Path.Combine(attempt, "launch.json"), JsonSerializer.Serialize(
                new GameTestLaunchIntent(1, executable, "steam", since, 1800)));
            // Current runner PID with a different start time cannot match and is never touched.
            File.WriteAllText(Path.Combine(attempt, "process.json"), JsonSerializer.Serialize(new
                { Id = Environment.ProcessId, StartUtc = since, Executable = executable, Route = "steam" }));
        }
        return attempt;
    }
}
