using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Game;

internal static class GameModResetChecks
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "mod-reset-checks", Guid.NewGuid().ToString("N"));
    private const string Chain = "The exception was: System.NullReferenceException: synthetic failed load\n at Example.Loader.Build()\n    - PREFIX unrelated.inventory: Void Other.Mod.Hook()\n at Verse.PlayDataLoader.DoPlayLoad()\n";
    private static void Assert(bool value, string message = "Mod reset regression")
    { if (!value) throw new Exception(message); }
    private static string Folder()
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N"), "中文 测试");
        Directory.CreateDirectory(path);
        return path;
    }
    internal static void Run(Action<string, Action> check)
    {
        check("Reset marker survives split writes and does not match ordinary errors", () =>
        {
            var path = Path.Combine(Folder(), "Player.log");
            var detector = new GameModResetDetection();
            File.WriteAllText(path, "Exception: ordinary red\n"); detector.Poll(path);
            Assert(!detector.Detected);
            foreach (var letter in GameModResetDetection.Marker)
            { File.AppendAllText(path, letter.ToString()); detector.Poll(path); }
            Assert(detector.Detected && !detector.RecoveryFinished);
            File.AppendAllText(path, "\n" + Chain + GameModResetDetection.Recovered);
            detector.Poll(path);
            Assert(detector.RecoveryFinished);
        });
        check("Runtime reset polling rejects a foreign PID and accepts only the correlated attempt", () =>
        {
            var data = Folder(); var path = Path.Combine(data, "runtime.log");
            var executable = Path.Combine(data, "game.exe");
            var started = DateTime.UtcNow.AddSeconds(-2);
            void Write(int pid) => File.WriteAllText(path, $"=== Waylith log ===\nProcess: pid {pid}\nCommand: \"{executable}\" -savedatafolder=\"{data}\" -logFile \"{Path.Combine(data, "Player.log")}\"\n\n" + GameModResetDetection.Marker);
            var capture = new GameTestLogCapture(executable, data, 1234, started, path);
            var detector = new GameModResetDetection();
            Write(4321); capture.PollReset(detector); Assert(!detector.Detected);
            Write(1234); capture.PollReset(detector); Assert(detector.Detected);
        });
        check("Reset exception in the middle of an abbreviated noisy log retains blocking priority", () =>
        {
            var data = Folder(); var path = Path.Combine(data, "Player.log");
            using (var writer = new StreamWriter(path))
            {
                for (var i = 0; i < 10000; i++) writer.WriteLine("INFO " + new string('a', 1000));
                for (var i = 0; i < 2100; i++) writer.WriteLine("Error " + i + " noise");
                writer.WriteLine(GameModResetDetection.Marker); writer.WriteLine(Chain);
                writer.WriteLine(GameModResetDetection.Recovered);
                for (var i = 0; i < 10000; i++) writer.WriteLine("INFO " + new string('b', 1000));
            }
            var log = PlayerLogReader.Read(path);
            Assert(log.ContextAbbreviated && !log.Text.Contains(GameModResetDetection.Marker)
                && log.ModResetError!.Contains("Example.Loader.Build"));
            var report = new OrdinaryGameReport(1, 0, GameTestStopReason.ModsReset, true, true, [log]);
            var context = DiagnosticLogContext.Create(report, new ModCatalogue([]), [], CancellationToken.None);
            Assert(context.BlockingError!.Contains("Example.Loader.Build")
                && context.Excerpts[0].Text.Contains(GameModResetDetection.Marker));
            Assert(DiagnosticLogContext.BlockingSymbols([log]).SequenceEqual(["Example.Loader.Build"]));
            var feedback = OrdinaryGameTestSession.CreateFeedback(report, context, data);
            Assert(feedback.CanDiagnose && !feedback.StartupVerified && feedback.StartupFailure!.Context.Stage == "ModsReset");
            Assert(!(report with { StopReason = GameTestStopReason.ProbeCompleted, ProbeStage = "map-ready", ProbeTicks = 120,
                ProbeColonists = 1, MenuPassed = true, FactionsPassed = true, PawnsTested = 1 }).MapSuccessVerified);
        });
        check("Running reset attempt ends, preserves orders and all exception output, then accepts a clean retest", () =>
        {
            var data = Folder();
            string[] order = ["ludeon.rimworld", "test.candidate", GameProbe.PackageId];
            GameTestLaunch.WriteConfiguration(data, "1.7", order, []);
            var before = File.ReadAllBytes(Path.Combine(data, "Config", "ModsConfig.xml"));
            var report = RunProbe(data, "reset");
            Assert(report.StopReason == GameTestStopReason.ModsReset && report.ProcessStopped && report.CaptureCompleted
                && report.ResetConfigurationCaptured == true && report.ProbeStage == "mods-reset");
            Assert(File.ReadAllBytes(Path.Combine(data, "intended-ModsConfig.xml")).SequenceEqual(before));
            Assert(!File.ReadAllText(Path.Combine(data, "observed-ModsConfig.xml")).Contains("test.candidate"));
            Assert(report.Logs.Any(l => l.Text.Contains("late diagnostic after reset")));
            var context = DiagnosticLogContext.Create(report, new ModCatalogue([]), order, CancellationToken.None);
            Assert(OrdinaryGameTestSession.CreateFeedback(report, context, data).CanDiagnose);
            var next = Folder(); GameTestLaunch.WriteConfiguration(next, "1.7", order, []);
            GameProbe.Prepare(next, order);
            var clean = RunProbe(next, "clean");
            Assert(clean.MapSuccessVerified && File.ReadAllText(Path.Combine(next, "intended-ModsConfig.xml")).Contains("test.candidate"));
            Assert(order.Contains("test.candidate"));
        });
        check("Ordinary errors continue collecting through the actual end of the attempt", () =>
        {
            var data = Folder(); var report = RunProbe(data, "ordinary");
            Assert(report.StopReason == GameTestStopReason.Exited && !report.StoppedByTester
                && report.Logs.Any(l => l.Text.Contains("late diagnostic after ordinary error")));
        });
        check("Reset on fast exit still overrides a nominally clean process exit", () =>
        {
            var data = Folder(); var report = RunProbe(data, "fast-reset");
            Assert(report.StopReason == GameTestStopReason.ModsReset && !report.MapSuccessVerified);
        });
    }

    private static OrdinaryGameReport RunProbe(string data, string mode)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RimWise.SteamChecks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--mod-reset-probe"); info.ArgumentList.Add(data); info.ArgumentList.Add(mode);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        return new OrdinaryGameRunner().RunAsync(info, data, new ModCatalogue([]), TimeSpan.FromSeconds(15),
            null, deadline.Token, warnOnSlowStartup: true).GetAwaiter().GetResult();
    }

    internal static async Task Probe(string data, string mode)
    {
        var path = Path.Combine(data, "Player.log");
        if (mode == "clean")
        {
            File.WriteAllText(path, "INFO clean simulated game\n");
            new XElement("signal", new XAttribute("nonce", GameProbe.ReadNonce(data)!), new XAttribute("pid", Environment.ProcessId),
                new XAttribute("stage", "map-ready"), new XAttribute("ticks", 120), new XAttribute("colonists", 1),
                new XAttribute("errors", 0), new XAttribute("menu", 1), new XAttribute("factions", 1), new XAttribute("pawns", 1))
                .Save(Path.Combine(data, "workbench-signal.xml"));
            return;
        }
        File.WriteAllText(path, "Error: synthetic startup error\n");
        if (mode == "ordinary")
        {
            await Task.Delay(1500);
            File.AppendAllText(path, "late diagnostic after ordinary error\n");
            return;
        }
        GameTestLaunch.WriteConfiguration(data, "1.7", ["ludeon.rimworld"], []);
        File.AppendAllText(path, GameModResetDetection.Marker + "\n" + Chain);
        if (mode == "fast-reset") return;
        await Task.Delay(600);
        File.AppendAllText(path, "late diagnostic after reset\n" + GameModResetDetection.Recovered + "\n");
        // The runner must stop its owned synthetic child even with no helper/total-duration timer.
        await Task.Delay(30000);
    }

    internal static void Replay(string directory)
    {
        var fingerprints = new HashSet<string>(); var count = 0;
        foreach (var data in Directory.EnumerateDirectories(directory, "attempt-*"))
        {
            var log = PlayerLogReader.Read(Path.Combine(data, "Player.log"));
            Assert(log.ModResetError is not null, "Archived reset missing");
            var report = new OrdinaryGameReport(1, 0, GameTestStopReason.ModsReset, true, true, [log]);
            var context = DiagnosticLogContext.Create(report, new ModCatalogue([]), [], CancellationToken.None);
            var feedback = OrdinaryGameTestSession.CreateFeedback(report, context, data);
            Assert(feedback.CanDiagnose && DiagnosticLogContext.BlockingSymbols([log]).Any(s => s.StartsWith("BigAndSmall.RaceFuser.")));
            fingerprints.Add(MaintenanceFailureSignature.Create(feedback)!); count++;
        }
        Assert(count == 2 && fingerprints.Count == 1, "Both archived attempts must retain the same blocking failure");
        Console.WriteLine(JsonSerializer.Serialize(new { ArchivedAttempts = count, BlockingFailures = fingerprints.Count, RealGameRetest = false }));
    }
}
