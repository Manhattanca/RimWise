using System.IO.Compression;
using System.Text.Json;
using RimWise.Integration.AI;
using RimWise.Integration.Diagnostics;

internal static class EmergencyDiagnosticsChecks
{
    internal static void Run(Action<string, Action> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "独立诊断 测试", Guid.NewGuid().ToString("N"));
        var anchor = Path.Combine(root, "anchor");
        var profile = Path.Combine(root, "迁移后 用户数据");
        var logs = Path.Combine(profile, "Data", "Diagnostics");
        Directory.CreateDirectory(anchor); Directory.CreateDirectory(logs);
        check("Location discovery reads Current without applying pending migration", () =>
        {
            Assert(EmergencyDiagnostics.ResolveProfile(anchor) == Path.Combine(anchor, "UserData"));
            File.WriteAllText(Path.Combine(anchor, "user-data-location.json"), JsonSerializer.Serialize(new {
                Current = profile, Pending = Path.Combine(root, "pending"), CleanupSource = Path.Combine(root, "old") }));
            Assert(EmergencyDiagnostics.ResolveProfile(anchor) == profile && !Directory.Exists(Path.Combine(root, "pending")));
        });
        const string secret = "synthetic-secret-not-a-real-api-key";
        new AiSettingsStore(profile).Save(AiSettings.Default);
        new LocalEncryptedSecretStore(profile).Write(AiSettings.Default.SecretName, secret);
        var log = Path.Combine(logs, "ui-20261105-010101-example.jsonl");
        File.WriteAllText(log, JsonSerializer.Serialize(new { area = "interface", operation = "menu-clicked", detail = "sample " + secret }) + "\n");
        try { throw new InvalidOperationException("message must not escape " + secret); }
        catch (Exception ex) { CrashEvidence.Write(profile, ex); }
        check("Crash structure is durable and excludes exception messages", () =>
        {
            var value = File.ReadAllText(Directory.GetFiles(logs, "crash-*.json").Single());
            Assert(value.Contains("InvalidOperationException") && !value.Contains(secret) && !value.Contains("message must"));
        });
        check("Export works with an open log, redacts keys, and leaves profile bytes unchanged", () =>
        {
            // UI prefixes sort after crash prefixes; retention must use actual recency.
            for (var i = 0; i < 21; i++)
            {
                var old = Path.Combine(logs, $"ui-20260101-{i:000000}-old.jsonl");
                File.WriteAllText(old, "{}\n");
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
            }
            var before = Snapshot(profile);
            using var writer = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            var destination = Path.Combine(root, "完整诊断.zip");
            Assert(EmergencyDiagnostics.Export(profile, destination, root));
            var entries = Read(destination);
            Assert(entries.ContainsKey("token-summary.json") && entries.Keys.Any(k => k.Contains("crash-")));
            Assert(entries.Values.Any(v => v.Contains("menu-clicked")) && entries.Values.All(v => !v.Contains(secret)));
            Assert(entries.Keys.All(k => !k.Contains("ai-settings") && !k.Contains("ai-secret")));
            Assert(before.SequenceEqual(Snapshot(profile)));
        });
        check("Unreadable stored key generates safe limited evidence without raw logs", () =>
        {
            File.WriteAllBytes(Path.Combine(profile, "Data", "ai-secret.bin"), [1, 2, 3]);
            var destination = Path.Combine(root, "密钥损坏.zip");
            Assert(!EmergencyDiagnostics.Export(profile, destination, root));
            var entries = Read(destination);
            Assert(entries["manifest.json"].Contains("\"limited\":true") && entries["crash-structure.json"].Contains("InvalidOperationException"));
            Assert(!entries.Keys.Any(k => k.Contains("ui-")) && entries.Values.All(v => !v.Contains(secret)));
        });
        check("Damaged settings still produce a diagnostic package and unknown token status", () =>
        {
            File.WriteAllText(Path.Combine(profile, "Data", "ai-settings.json"), "{bad");
            var destination = Path.Combine(root, "配置损坏.zip");
            Assert(!EmergencyDiagnostics.Export(profile, destination, root));
            Assert(Read(destination)["token-summary.json"].Contains("\"available\":false"));
        });
        check("Missing profile exports a limited report without creating profile data", () =>
        {
            var missing = Path.Combine(root, "missing");
            Assert(!EmergencyDiagnostics.Export(missing, Path.Combine(root, "missing.zip"), root));
            Assert(!Directory.Exists(missing));
        });
        check("Existing destination and output inside profile are rejected", () =>
        {
            var destination = Path.Combine(root, "完整诊断.zip");
            var before = File.ReadAllBytes(destination);
            try { EmergencyDiagnostics.Export(profile, destination, root); throw new Exception("overwrote report"); } catch (IOException) { }
            Assert(before.SequenceEqual(File.ReadAllBytes(destination)));
            try { EmergencyDiagnostics.Export(profile, Path.Combine(profile, "report.zip"), root); throw new Exception("wrote profile"); } catch (IOException) { }
        });
        check("Broken location pointer remains untouched and unresolved location can be reported", () =>
        {
            var path = Path.Combine(anchor, "user-data-location.json");
            File.WriteAllText(path, "{broken");
            try { EmergencyDiagnostics.ResolveProfile(anchor); throw new Exception("guessed location"); } catch (JsonException) { }
            Assert(File.ReadAllText(path) == "{broken");
            Assert(!EmergencyDiagnostics.Export(null, Path.Combine(root, "location.zip"), root, "JsonException"));
        });
    }
    private static Dictionary<string, string> Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); });
    }
    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order()
        .Select(p => { using var input = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return p + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)); }).ToArray();
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Emergency diagnostic check failed."); }
}
