using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RimWise.Core;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Game;

internal static class LargeLogChecks
{
    private static void Assert(bool condition) { if (!condition) throw new Exception("Large log regression"); }
    private static readonly string Padding = "2026-11-01 01:00:00.000 INFO " + new string('x', 8000) + "\n";
    internal static async Task EmitAsync(string directory)
    {
        // Synthetic log writer only. No game, network, DLL generation or player data.
        var bytes = Encoding.UTF8.GetBytes(Padding);
        using var player = File.Create(Path.Combine(directory, "Player.log"));
        var stdout = Console.OpenStandardOutput();
        var stderr = Console.OpenStandardError();
        for (var i = 0; i < 2300; i++)
        {
            await player.WriteAsync(bytes);
            await stdout.WriteAsync(bytes);
            await stderr.WriteAsync(bytes);
        }
        await player.FlushAsync();
        await Task.Delay(1250);
        var end = Encoding.UTF8.GetBytes("2026-11-01 01:00:01.000 INFO COMPLETE_LOG_SENTINEL\n");
        await player.WriteAsync(end); await stdout.WriteAsync(end); await stderr.WriteAsync(end);
        File.WriteAllText(Path.Combine(directory, "probe-finished.txt"), "finished");
    }

    internal static void Run(Action<string, Action> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "大日志 回归", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "runtime.log");
        check("Large logs scan early, middle and final errors without truncating the capture", () =>
        {
            using (var writer = new StreamWriter(source, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("2026-11-01 01:00:00.000 ERROR EarlyError");
                for (var i = 0; i < 2200; i++) writer.Write(Padding);
                writer.WriteLine("2026-11-01 01:00:00.000 ERROR MiddleError");
                for (var i = 0; i < 2200; i++) writer.Write(Padding);
                writer.WriteLine("2026-11-01 01:00:00.000 ERROR FinalError");
                writer.WriteLine("2026-11-01 01:00:00.000 WARN FinalWarning");
            }
            var snapshot = PlayerLogReader.Read(source);
            Assert(new FileInfo(source).Length > 32 * 1024 * 1024 && !snapshot.Truncated && snapshot.ContextAbbreviated);
            Assert(snapshot.Issues.Select(i => i.Summary).ToHashSet().SetEquals(["EarlyError", "MiddleError", "FinalError"]));
            Assert(snapshot.Warnings.Single().Summary == "FinalWarning");
            Assert(!snapshot.Text.Contains("MiddleError") && snapshot.Text.Contains("FinalWarning"));
            var report = new OrdinaryGameReport(1, 0, GameTestStopReason.ProbeCompleted, true, true, [snapshot])
            { ProbeStage = "map-ready", ProbeTicks = 120, ProbeColonists = 1, MenuPassed = true, FactionsPassed = true, PawnsTested = 1 };
            var feedback = OrdinaryGameTestSession.CreateFeedback(report, new([], [], false), root);
            Assert(feedback.CanDiagnose && feedback.Errors.Count > 0);
            Assert(!(report with { ProbeStage = null }).MapSuccessVerified);
            var clean = report with { Logs = [snapshot with { Issues = [] }] };
            Assert(clean.MapSuccessVerified);
        });
        check("Complete large logs survive compression byte for byte", () =>
        {
            byte[] hash;
            using (var file = File.OpenRead(source)) hash = SHA256.HashData(file);
            StoredEvidence.Compress(source);
            using var input = File.OpenRead(source + ".gz");
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            Assert(hash.SequenceEqual(SHA256.HashData(gzip)));
        });
        check("Correlated runtime capture copies beyond the former per-attempt quota", () =>
        {
            var runtime = Path.Combine(root, "correlated.log");
            var started = DateTime.UtcNow.AddSeconds(-1);
            var executable = Environment.ProcessPath!;
            using (var writer = new StreamWriter(runtime, false, new UTF8Encoding(false)))
            {
                writer.Write($"=== Waylith log ===\nProcess: pid {Environment.ProcessId}\nCommand: \"{executable}\" -savedatafolder=\"{root}\" -logFile \"{Path.Combine(root, "Player.log")}\"\n\n");
                for (var i = 0; i < 2300; i++) writer.Write(Padding);
                writer.Write("END_OF_CORRELATED_LOG");
            }
            var capture = new GameTestLogCapture(executable, root, Environment.ProcessId, started, runtime);
            var destination = Path.Combine(root, "copy.log");
            Assert(capture.CopyCorrelated(destination));
            using var original = File.OpenRead(runtime); using var copied = File.OpenRead(destination);
            Assert(original.Length > 16 * 1024 * 1024 && SHA256.HashData(original).SequenceEqual(SHA256.HashData(copied)));
        });
        check("A growing log snapshot terminates at its observed length and detects lost bytes", () =>
        {
            using var content = new MemoryStream(new byte[10]);
            using var snapshot = new LogSnapshotStream(content, 5);
            using var output = new MemoryStream(); snapshot.CopyTo(output);
            Assert(output.Length == 5);
            content.Position = 0;
            using var missing = new LogSnapshotStream(content, 11);
            try { missing.CopyTo(Stream.Null); }
            catch (IOException) { return; }
            throw new Exception("Shortened log was accepted");
        });
        check("Automatic test runner lets all three logs exceed 16 MiB and waits for normal completion", () =>
        {
            var data = Path.Combine(root, "process"); Directory.CreateDirectory(data);
            var info = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--log-volume-probe"); info.ArgumentList.Add(data);
            var report = new OrdinaryGameRunner().RunAsync(info, data, new ModCatalogue([]), TimeSpan.FromSeconds(30), null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(report.StopReason == GameTestStopReason.Exited && report.ExitCode == 0 && report.CaptureCompleted && !report.StoppedByTester);
            Assert(File.Exists(Path.Combine(data, "probe-finished.txt")) && !report.MapSuccessVerified);
            foreach (var name in new[] { "Player.log", "stdout.log", "stderr.log" })
            {
                var path = Path.Combine(data, name);
                Assert(new FileInfo(path).Length > 16 * 1024 * 1024);
                Assert(report.Logs.Single(l => l.Path == path).Text.Contains("COMPLETE_LOG_SENTINEL"));
                StoredEvidence.Compress(path);
            }
        });
    }
}
