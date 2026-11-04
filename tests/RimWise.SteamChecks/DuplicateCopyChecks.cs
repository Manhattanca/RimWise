using RimWise.Core;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;
using RimWise.Integration.AI;
using System.Text.Json;

internal static class DuplicateCopyChecks
{
    internal static void Run(Action<string, Action> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "副本选择 合成", Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(root, "Mods");
        ModInstallation Mod(string folder, string release, bool bad = false)
        {
            var directory = Path.Combine(mods, folder);
            Directory.CreateDirectory(Path.Combine(directory, "About"));
            Directory.CreateDirectory(Path.Combine(directory, "Defs"));
            File.WriteAllText(Path.Combine(directory, "About", "About.xml"),
                $"<ModMetaData><name>中文 副本</name><packageId>sample.copy</packageId><modVersion>{release}</modVersion><supportedVersions><li>1.7</li></supportedVersions></ModMetaData>");
            File.WriteAllText(Path.Combine(directory, "Defs", "sample.xml"), bad ? "<Defs>" : "<Defs/>");
            var metadata = new ModMetadataReader().Read(Path.Combine(directory, "About", "About.xml"), directory, new(1, 7));
            return new(directory, folder, ModSource.Local, "", metadata, []);
        }
        var a = Mod("A 高版本", "2.0"); var b = Mod("B 旧版本", "1.9");
        var paths = new GamePaths { LocalModsDirectory = mods };
        var store = new DuplicateCopyBackups(Path.Combine(root, "用户 数据"), paths);
        DuplicateCopyInspection Inspect(ModInstallation m) => DuplicateCopyInspector.Inspect(m, new(1, 7), new HashSet<string>(), CancellationToken.None);
        void Assert(bool value) { if (!value) throw new Exception("Duplicate copy regression"); }
        check("Release version is distinct from supported game version", () =>
            Assert(a.Metadata!.ReleaseVersion == "2.0" && a.Metadata.SupportedVersions.Single() == "1.7"));
        check("Readable higher mod release wins regardless of input enumeration", () =>
            Assert(DuplicateCopyInspector.Rank([Inspect(b), Inspect(a)])[0].Installation == a));
        check("Malformed higher release recommends readable lower copy", () =>
        {
            var broken = Mod("损坏", "9.0", true);
            Assert(Inspect(broken).HasFileErrors && DuplicateCopyInspector.Rank([Inspect(broken), Inspect(b)])[0].Installation == b);
        });
        check("Same release tie uses stable ordinal folder order; unknown is not game version", () =>
        {
            var c = Mod("C 同版本", "2.0"); var unknown = Mod("未知", "");
            Assert(DuplicateCopyInspector.Rank([Inspect(c), Inspect(a)])[0].Installation == a);
            Assert(Inspect(unknown).Version == "");
        });
        DuplicateCopyBackup? plan = null;
        check("Planning or declining changes no mod files and creates no journal", () =>
        {
            plan = store.Plan([a, b], a);
            Assert(Directory.Exists(a.Directory) && Directory.Exists(b.Directory) && store.List().Count == 0);
        });
        check("Cancelled copy operation leaves both installations untouched", () =>
        {
            try { store.Apply(plan!, new CancellationToken(true)); }
            catch (OperationCanceledException) { }
            Assert(Directory.Exists(a.Directory) && Directory.Exists(b.Directory) && store.List().Count == 0);
        });
        check("Confirmed move journals backup outside scan root without changing content", () =>
        {
            var text = File.ReadAllText(Path.Combine(b.Directory, "About", "About.xml"));
            store.Apply(plan!, CancellationToken.None);
            Assert(Directory.Exists(a.Directory) && !Directory.Exists(b.Directory) && store.List().Count == 1);
            Assert(File.ReadAllText(Path.Combine(plan!.Moves[0].Backup, "About", "About.xml")) == text);
            Assert(!plan.Moves[0].Backup.StartsWith(mods + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        });
        check("Steam recreated source blocks restore without overwrite", () =>
        {
            Directory.CreateDirectory(b.Directory);
            try { store.Restore(plan!.Id, CancellationToken.None); throw new Exception("Restore overwrote collision"); }
            catch (IOException) { Assert(Directory.Exists(plan!.Moves[0].Backup)); }
            Directory.Delete(b.Directory); // Exact empty synthetic directory, never recursive.
        });
        check("New store instance restores journalled Chinese paths", () =>
        {
            new DuplicateCopyBackups(Path.Combine(root, "用户 数据"), paths).Restore(plan!.Id, CancellationToken.None);
            Assert(Directory.Exists(a.Directory) && Directory.Exists(b.Directory) && store.List().Count == 0);
        });
        check("Foreign directories and official content cannot enter move plan", () =>
        {
            foreach (var mod in new[] { b with { Source = ModSource.Game }, b with { Directory = root } })
            {
                try { store.Plan([a, mod], a); throw new Exception("Unsafe source accepted"); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { }
            }
        });
        check("Malformed optional release metadata does not hide an otherwise valid mod", () =>
        {
            var optional = Mod("可选 版本", "<nested>1.0</nested>");
            Assert(optional.Metadata!.ReleaseVersion == "" && optional.PackageId == "sample.copy");
        });
        check("Higher release remains recommended when inspection is incomplete, with status retained", () =>
        {
            var incomplete = Inspect(a) with { Limited = true };
            Assert(DuplicateCopyInspector.Rank([Inspect(b), incomplete])[0] == incomplete);
        });
        check("Backup journal rejects nested destination substitution before moving files", () =>
        {
            var unsafePlan = store.Plan([a, b], a);
            var destination = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(unsafePlan.Moves[0].Backup))!,
                "unexpected", unsafePlan.Id, "0");
            try { store.Apply(unsafePlan with { Moves = [new(b.Directory, destination)] }, CancellationToken.None);
                throw new Exception("Nested destination accepted"); }
            catch (InvalidDataException) { }
            Assert(Directory.Exists(b.Directory) && !Directory.Exists(destination));
        });
        check("Interrupted multi-copy move can restore completed moves without touching unmoved copy", () =>
        {
            var c = Mod("第三 副本", "1.8");
            var partial = store.Plan([a, b, c], a);
            var journal = Path.Combine(root, "用户 数据", "Data", "DuplicateCopies", partial.Id + ".json");
            File.WriteAllText(journal, JsonSerializer.Serialize(partial));
            Directory.CreateDirectory(Path.GetDirectoryName(partial.Moves[0].Backup)!);
            Directory.Move(b.Directory, partial.Moves[0].Backup);
            store.Restore(partial.Id, CancellationToken.None);
            Assert(Directory.Exists(b.Directory) && Directory.Exists(c.Directory) && store.List().Count == 0);
        });
        check("Damaged backup JSON produces a recoverable data error", () =>
        {
            var journal = Path.Combine(root, "用户 数据", "Data", "DuplicateCopies", "damaged.json");
            File.WriteAllText(journal, "{");
            try { store.List(); throw new Exception("Malformed journal accepted"); }
            catch (InvalidDataException) { }
            finally { File.Delete(journal); }
        });
        var limits = new MaintenanceLimits(5, 1000) { AttemptLimitEnabled = true, TokenLimitEnabled = true };
        var taskState = new AiTaskCheckpoint(AiTaskKind.Maintenance, AiTaskPhase.Paused, new string('A', 64),
            ["sample.copy"], ["sample.copy"], DateTimeOffset.UtcNow)
            { Attempts = 2, ReportedTokens = 300, ReservedTokens = 200, Limits = limits, DeadlineUtc = DateTimeOffset.UtcNow.AddHours(1) };
        check("Copy retry carries both actual and reserved tokens and remaining test budget", () =>
        {
            var remaining = DuplicateCopyRetry.Remaining(limits, taskState);
            Assert(remaining is { MaximumAttempts: 3, TokenBudget: 500 });
            Assert(DuplicateCopyRetry.Remaining(limits, taskState with { ReservedTokens = 700 }) is null);
            Assert(DuplicateCopyRetry.Remaining(limits, taskState with { Attempts = 5 }) is null);
            Assert(DuplicateCopyRetry.Remaining(limits, taskState with { Phase = AiTaskPhase.RequestPending }) is null);
        });
        check("Confirmed task retirement persists and preserves billed usage without replay", () =>
        {
            var tasks = new AiTaskRecoveryStore(Path.Combine(root, "任务"));
            string id;
            using (var task = tasks.Create(taskState))
            { id = task.Id; DuplicateCopyRetry.Retire(task); }
            using var restored = tasks.Open(id);
            Assert(restored.Current.Finished && restored.Current.Discarded && restored.Current.ReportedTokens == 300
                && restored.Current.ReservedTokens == 200 && DuplicateCopyRetry.Remaining(limits, restored.Current) is null);
        });
    }
}
