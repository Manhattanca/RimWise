using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Diagnostics;

internal static class StorageMigrationChecks
{
    internal static void Run(Action<string, Action> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "迁移 [检查]", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Area() { var path = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        static void Assert(bool value) { if (!value) throw new Exception("Storage migration regression"); }
        static void Write(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
        static void Archive(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write("{\"format\":\"RimWiseDiagnostic/1\"}");
            using (var writer = new StreamWriter(zip.CreateEntry("example.log").Open())) writer.Write(value);
        }
        check("Profile migration removes verified originals, preserves unrelated files and encrypted key", () =>
        {
            var area = Area(); var anchor = Path.Combine(area, "locator"); var install = Path.Combine(area, "app");
            var target = Path.Combine(area, "新 数据"); string original;
            using (var location = new UserDataLocation(anchor, install))
            {
                original = location.DirectoryPath;
                Write(Path.Combine(original, "Data", "nested", "log.txt"), "evidence");
                Write(Path.Combine(original, "personal.txt"), "keep");
                new LocalEncryptedSecretStore(original).Write("test", "SyntheticMigrationSecret");
                location.PrepareMove(target);
                Assert(location.DirectoryPath == original);
            }
            using (var moved = new UserDataLocation(anchor, install))
            {
                Assert(moved.DirectoryPath == target && !Directory.Exists(Path.Combine(original, "Data")));
                Assert(File.ReadAllText(Path.Combine(original, "personal.txt")) == "keep");
                Assert(new LocalEncryptedSecretStore(target).Read("test") == "SyntheticMigrationSecret");
                Assert(StoredEvidence.ReadText(Path.Combine(original, "Data", "nested", "log.txt"), 1024) == "evidence");
                Assert(new AutomaticSortingLogStore(target).LoadSettings().Directory == Path.Combine(anchor, "RimWise-Logs"));
                moved.PrepareMove(Path.Combine(area, "third"));
            }
            using var again = new UserDataLocation(anchor, install);
            Assert(!Directory.Exists(target) && StoredEvidence.ReadText(Path.Combine(original, "Data", "nested", "log.txt"), 1024) == "evidence");
        });
        check("Profile move refuses live peers and cancellation keeps the original profile", () =>
        {
            var area = Area(); var anchor = Path.Combine(area, "locator"); var install = Path.Combine(area, "app");
            using var first = new UserDataLocation(anchor, install);
            using (var peer = new UserDataLocation(anchor, install))
            {
                var refused = false;
                try { first.PrepareMove(Path.Combine(area, "target")); } catch (IOException) { refused = true; }
                Assert(refused && peer.DirectoryPath == first.DirectoryPath);
            }
            first.PrepareMove(Path.Combine(area, "target")); first.CancelMove();
            using var next = new UserDataLocation(anchor, install);
            Assert(next.DirectoryPath == first.DirectoryPath);
        });
        check("Profile copy failure retains original contents and current locator", () =>
        {
            var area = Area(); var anchor = Path.Combine(area, "locator"); var install = Path.Combine(area, "app");
            var target = Path.Combine(area, "target"); string original;
            using (var location = new UserDataLocation(anchor, install))
            { original = location.DirectoryPath; Write(Path.Combine(original, "Data", "keep.txt"), "original"); location.PrepareMove(target); }
            Write(Path.Combine(target, "Data", "keep.txt"), "unrelated");
            using var failed = new UserDataLocation(anchor, install);
            Assert(failed.DirectoryPath == original && File.ReadAllText(Path.Combine(original, "Data", "keep.txt")) == "original");
            Assert(File.ReadAllText(Path.Combine(target, "Data", "keep.txt")) == "unrelated" && failed.StartupNotice!.Contains("失败"));
        });
        check("Interrupted profile staging and committed cleanup resume without losing evidence", () =>
        {
            var area = Area(); var anchor = Path.Combine(area, "locator"); var install = Path.Combine(area, "app");
            var target = Path.Combine(area, "target"); string original;
            using (var location = new UserDataLocation(anchor, install))
            { original = location.DirectoryPath; Write(Path.Combine(original, "Data", "keep.txt"), "complete"); location.PrepareMove(target); }
            Write(Path.Combine(target, ".rimwise-migration", "keep.txt"), "partial");
            using (var moved = new UserDataLocation(anchor, install))
                Assert(moved.DirectoryPath == target && File.ReadAllText(Path.Combine(target, "Data", "keep.txt")) == "complete");
            Write(Path.Combine(original, "Data", "keep.txt"), "complete");
            Write(Path.Combine(anchor, "user-data-location.json"), JsonSerializer.Serialize(new { Current = target, CleanupSource = original, PreviousRoots = new[] { original } }));
            using var recovered = new UserDataLocation(anchor, install);
            Assert(!Directory.Exists(Path.Combine(original, "Data")) && File.Exists(Path.Combine(target, "Data", "keep.txt")));
        });
        check("Changed profile originals survive interrupted cleanup", () =>
        {
            var area = Area(); var anchor = Path.Combine(area, "locator"); var original = Path.Combine(area, "old"); var target = Path.Combine(area, "new");
            Write(Path.Combine(original, "Data", "keep.txt"), "changed"); Write(Path.Combine(target, "Data", "keep.txt"), "copied");
            Write(Path.Combine(anchor, "user-data-location.json"), JsonSerializer.Serialize(new { Current = target, CleanupSource = original }));
            using var location = new UserDataLocation(anchor, Path.Combine(area, "app"));
            Assert(location.DirectoryPath == target && File.ReadAllText(Path.Combine(original, "Data", "keep.txt")) == "changed");
            Assert(location.StartupNotice!.Contains("清理未完成"));
        });
        check("Log move preserves name collisions, deduplicates exact copies and retains unrelated files", () =>
        {
            var area = Area(); var store = new AutomaticSortingLogStore(Path.Combine(area, "profile"));
            var source = Path.Combine(area, "原 日志"); var target = Path.Combine(area, "新 日志");
            store.SaveSettings(new(true, source));
            Archive(Path.Combine(source, "RimWise-sorting-collision.zip"), "source");
            Archive(Path.Combine(target, "RimWise-sorting-collision.zip"), "target");
            Archive(Path.Combine(source, "RimWise-diagnostics-same.zip"), "same");
            File.Copy(Path.Combine(source, "RimWise-diagnostics-same.zip"), Path.Combine(target, "RimWise-diagnostics-same.zip"));
            Write(Path.Combine(source, "personal.txt"), "keep"); Write(Path.Combine(source, "RimWise-sorting-invalid.zip"), "incomplete");
            var result = store.ChangeDirectory(target);
            Assert(result.Moved == 2 && result.Preserved == 1 && !result.CleanupPending && store.LoadSettings().Directory == target);
            Assert(Directory.GetFiles(target, "*.zip").Length == 3 && Directory.GetFiles(source).Length == 2);
        });
        check("Interrupted log move resumes staged copy and cleans originals only after commit", () =>
        {
            var area = Area(); var profile = Path.Combine(area, "profile"); var source = Path.Combine(area, "old"); var target = Path.Combine(area, "new");
            var name = "RimWise-sorting-example.zip"; var input = Path.Combine(source, name);
            var store = new AutomaticSortingLogStore(profile); store.SaveSettings(new(false, source)); Archive(input, "complete");
            Write(Path.Combine(target, "." + name + ".rimwise-migration"), "partial");
            Write(Path.Combine(profile, "Data", "automatic-log-migration.json"), JsonSerializer.Serialize(new { Move = new {
                Source = source, Destination = target, Files = new[] { new { Name = name, DestinationName = name, Hash = Hash(input) } }, Preserved = 0, Committed = false } }));
            var result = store.ResumeDirectoryMove();
            Assert(result is { Moved: 1, CleanupPending: false } && !File.Exists(input) && store.LoadSettings().Directory == target);
            Assert(store.ResumeDirectoryMove() is null);
        });
        check("Locked original log is preserved and cleanup resumes on next attempt", () =>
        {
            var area = Area(); var profile = Path.Combine(area, "profile"); var source = Path.Combine(area, "old"); var target = Path.Combine(area, "new");
            var input = Path.Combine(source, "RimWise-sorting-locked.zip");
            var store = new AutomaticSortingLogStore(profile); store.SaveSettings(new(false, source)); Archive(input, "locked");
            using (var held = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            { Assert(store.ChangeDirectory(target).CleanupPending && File.Exists(input)); }
            Assert(store.ResumeDirectoryMove() is { CleanupPending: false } && !File.Exists(input));
        });
        check("Invalid log destination leaves originals and saved path untouched", () =>
        {
            var area = Area(); var profile = Path.Combine(area, "profile"); var source = Path.Combine(area, "old"); var target = Path.Combine(area, "file");
            var store = new AutomaticSortingLogStore(profile); store.SaveSettings(new(false, source));
            Archive(Path.Combine(source, "RimWise-sorting-example.zip"), "keep"); Write(target, "personal");
            var refused = false;
            try { store.ChangeDirectory(target); } catch (ArgumentException) { refused = true; }
            Assert(refused && store.LoadSettings().Directory == source && Directory.GetFiles(source).Length == 1 && File.ReadAllText(target) == "personal");
        });
    }
}
