#if DEBUG
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using RimWise.Core;
using RimWise.Formats;
using RimWise.Integration.Configuration;
using RimWise.Integration.Marketplace;
using RimWise.Integration.Scanning;
using RimWise.Integration.Steam;

namespace RimWise.Desktop;

/// <summary>Runs only against disposable fixtures and injected preview delegates.</summary>
internal static class MarketplaceApplySmokeCheck
{
    internal static async Task RunAsync(MainWindow window, string output, List<string> checks)
    {
        var root = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(UserStorage.DirectoryPath).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Marketplace checks require an isolated smoke profile inside the output directory.");
        var pathField = typeof(MainWindow).GetField("paths", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var oldPaths = pathField.GetValue(window);
        var oldStorage = UserStorage.DirectoryPath;
        var originalFile = window.ViewModel.HasDocument ? window.ViewModel.CreateOrderFile() : null;
        var originalLabel = window.ViewModel.SourceLabel;
        try
        {
            var fixture = new Fixture(window, output, "成功 中文 [路径] &", pathField);
            var exported = MarketplaceOrderCompatibility.ToOrderFile(fixture.Manifest);
            var codec = new OrderFileCodec();
            var roundtrip = codec.Read(new MemoryStream(codec.Write(exported, OrderFileFormat.RmlXml)));
            Require(roundtrip.Configuration.GameVersion == fixture.Manifest.GameVersion
                && roundtrip.Configuration.Order.PackageIds.SequenceEqual(fixture.Manifest.Mods.Select(mod => mod.PackageId))
                && roundtrip.WorkshopReferences.Select(item => item.WorkshopId).SequenceEqual(["0", "123", "456", "789"]),
                "RML export lost order/version/Workshop references");
            checks.Add("Marketplace RML export roundtrip retains authored order, version and Workshop references");

            window.ViewModel.ToggleRetained(window.ViewModel.Rows.Single(row => row.PackageId == "old.mod"));
            var retention = window.ViewModel.RetainedPackageIds.ToArray();
            fixture.Capture();
            var previews = 0;
            var result = await ApplyAsync(window, fixture.Manifest, null, default,
                approvePreview: (_, notice, _) =>
                {
                    previews++;
                    Require(!notice.Contains("缺少") && !notice.Contains("前置") && !notice.Contains("诊断")
                        && notice.Contains("必须保留") && notice.Contains("old.mod") && notice.Contains("创意工坊下载已完成"),
                        "Import preview ran blocking diagnostics or omitted download completion/replacement safeguards");
                    return Task.FromResult(true);
                });
            Require(result && previews == 1 && !Directory.Exists(Path.Combine(fixture.Paths.WorkshopDirectory, "456"))
                && !Directory.Exists(Path.Combine(fixture.Paths.WorkshopDirectory, "789"))
                && window.ViewModel.Status.Contains("缺少 2") && window.ViewModel.HasOrderStatus && window.ViewModel.ErrorCount >= 2,
                "Import downloaded, blocked missing mod IDs or suppressed ordinary main-window warnings");
            var saved = File.ReadAllBytes(fixture.Config);
            Require(window.ViewModel.Rows.Select(row => row.PackageId).SequenceEqual(fixture.Manifest.Mods.Select(mod => mod.PackageId))
                && !window.ViewModel.HasChanges && window.ViewModel.RetainedPackageIds.SequenceEqual(retention)
                && Encoding.UTF8.GetString(saved).Contains("keep-this-field"), "Apply changed author order, retention or unrelated XML");
            Require(window.ViewModel.CreateOrderFile().WorkshopReferences.Select(item => item.WorkshopId).SequenceEqual(["0", "123", "456", "789"]),
                "Editor lost manifest Workshop references");
            window.ViewModel.UndoCommand.Execute(null);
            Require(File.ReadAllBytes(fixture.Config).SequenceEqual(fixture.OriginalBytes)
                && window.ViewModel.Rows.Select(row => row.PackageId).SequenceEqual(fixture.OriginalOrder)
                && window.ViewModel.HasChanges == fixture.WasDirty, "Combined undo failed exact-byte config/editor/dirty restore");
            window.ViewModel.RedoCommand.Execute(null);
            Require(File.ReadAllBytes(fixture.Config).SequenceEqual(saved) && !window.ViewModel.HasChanges,
                "Combined redo failed");
            Require(window.ViewModel.HasOrderStatus && window.ViewModel.Diagnostics.Count > 0, "Redo suppressed ordinary main-window warnings");
            window.ViewModel.MoveRowsTo([window.ViewModel.Rows[^1]], 1);
            Require(window.ViewModel.HasOrderStatus && window.ViewModel.Diagnostics.Count > 0, "Manual edits did not resume ordinary diagnostics");
            checks.Add("Marketplace import, redo and later manual edits retain ordinary main-window warnings");
            checks.Add("Marketplace import retains missing IDs and their notice without blocking diagnostics after Steam completion and supports exact undo/redo");

            foreach (var scenario in new[] { "decline", "cancel", "editor edit", "config edit", "path edit", "version edit",
                "late superseded", "backup failure", "publication failure" })
            {
                fixture = new Fixture(window, output, scenario, pathField);
                using var cancellation = new CancellationTokenSource();
                var publicationThrew = false;
                PropertyChangedEventHandler publication = (_, args) =>
                {
                    if (scenario == "publication failure" && !publicationThrew && args.PropertyName == nameof(window.ViewModel.SourceLabel))
                    { publicationThrew = true; throw new IOException("synthetic publication failure"); }
                };
                window.ViewModel.PropertyChanged += publication;
                byte[]? externalBytes = null;
                string[]? externalOrder = null;
                bool applied;
                try
                {
                    applied = await ApplyAsync(window, fixture.Manifest, null, cancellation.Token,
                        approvePreview: async (_, _, _) =>
                        {
                            fixture.Unchanged();
                            if (scenario == "cancel") cancellation.Cancel();
                            if (scenario == "editor edit")
                            {
                                window.ViewModel.MoveRowsTo([window.ViewModel.Rows[^1]], 1);
                                externalOrder = window.ViewModel.Rows.Select(row => row.PackageId).ToArray();
                            }
                            if (scenario == "config edit")
                            { externalBytes = Encoding.UTF8.GetBytes("external config edit"); File.WriteAllBytes(fixture.Config, externalBytes); }
                            if (scenario == "path edit") pathField.SetValue(window, fixture.Paths with { TargetVersionOverride = "1.6" });
                            if (scenario == "version edit") File.WriteAllText(Path.Combine(fixture.Paths.GameDirectory, "Version.txt"), "1.6.1");
                            if (scenario == "late superseded")
                                Require(!await ApplyAsync(window, fixture.Manifest, null, default,
                                    approvePreview: (_, _, _) => Task.FromResult(false)), "Superseding preview should decline");
                            if (scenario == "backup failure")
                            {
                                var backupRoot = Path.Combine(UserStorage.DirectoryPath, "Data", "GameBackups");
                                Directory.CreateDirectory(Path.GetDirectoryName(backupRoot)!);
                                File.WriteAllText(backupRoot, "backup-blocker");
                            }
                            return scenario != "decline";
                        });
                }
                finally { window.ViewModel.PropertyChanged -= publication; }
                Require(!applied, scenario + " incorrectly committed");
                Require(File.ReadAllBytes(fixture.Config).SequenceEqual(externalBytes ?? fixture.OriginalBytes), scenario + " overwrote configuration");
                Require(window.ViewModel.Rows.Select(row => row.PackageId).SequenceEqual(externalOrder ?? fixture.OriginalOrder), scenario + " changed editor");
                if (externalOrder is null)
                    Require(window.ViewModel.HasChanges == fixture.WasDirty
                        && window.ViewModel.UndoCommand.CanExecute(null) == fixture.CouldUndo
                        && window.ViewModel.RedoCommand.CanExecute(null) == fixture.CouldRedo, scenario + " changed history");
                if (scenario == "publication failure") Require(publicationThrew, "Publication failure was not exercised");
                checks.Add("Marketplace import " + scenario + " preserves original editor/history/config or intervening edits");
            }

            foreach (var scenario in new[] { "already installed wrong source", "missing dependency", "dependency after dependent", "wrong version", "old declared version", "multiple declared versions", "different editor version",
                "required DLC missing", "remove current DLC", "ambiguous installation", "unreadable metadata", "cached catalogue", "no core" })
            {
                fixture = new Fixture(window, output, scenario, pathField);
                var manifest = fixture.Manifest;
                fixture.Install(new("b.mod", "456")); fixture.Install(new("c.mod", "789"));
                if (scenario == "missing dependency") fixture.WriteMod("789", "c.mod", "absent.mod");
                if (scenario == "dependency after dependent") manifest = manifest with { Mods = [manifest.Mods[0], manifest.Mods[1], manifest.Mods[3], manifest.Mods[2]] };
                if (scenario == "wrong version") manifest = manifest with { GameVersion = "1.6" };
                if (scenario == "old declared version") manifest = manifest with { GameVersion = "0.18" };
                if (scenario == "multiple declared versions") manifest = manifest with { GameVersion = "1.6, 1.7" };
                if (scenario == "required DLC missing") manifest = manifest with { RequiredDlc = ["ludeon.rimworld.royalty"],
                    Mods = [manifest.Mods[0], new("ludeon.rimworld.royalty", null), .. manifest.Mods.Skip(1)] };
                if (scenario == "cached catalogue") window.ViewModel.IsShowingCache = true;
                if (scenario == "no core") manifest = manifest with { Mods = manifest.Mods.Skip(1).ToArray() };
                if (scenario == "remove current DLC")
                {
                    fixture.WriteGameContent("Royalty", "ludeon.rimworld.royalty");
                    var document = window.ViewModel.CreateExportDocument();
                    window.ViewModel.Load(document.WithOrder(new LoadOrder(["ludeon.rimworld", "ludeon.rimworld.royalty", "a.mod", "old.mod"])), fixture.Config);
                    fixture.Capture();
                }
                if (scenario == "different editor version")
                {
                    using var different = new MemoryStream(Encoding.UTF8.GetBytes("<ModsConfigData><version>0.18</version><activeMods><li>Core</li></activeMods></ModsConfigData>"));
                    window.ViewModel.Load(new ModsConfigXmlFormat().Read(different), fixture.Config);
                    fixture.Capture();
                }
                var current = await fixture.ScanAsync(default);
                if (scenario == "already installed wrong source")
                {
                    var wrong = current.Catalogue.Installations.Single(mod => mod.WorkshopId == "123") with { WorkshopId = "321" };
                    current = current with { Catalogue = new(current.Catalogue.Installations.Where(mod => mod.WorkshopId != "123").Append(wrong).ToArray()) };
                }
                if (scenario == "ambiguous installation")
                {
                    var duplicate = current.Catalogue.Installations.Single(mod => mod.WorkshopId == "123") with { Directory = Path.Combine(fixture.Paths.WorkshopDirectory, "321") };
                    current = current with { Catalogue = new(current.Catalogue.Installations.Append(duplicate).ToArray()) };
                }
                window.ViewModel.ApplyScan(current, observeActivity: false);
                // Files cease to be readable metadata after setup: import must use only the order file.
                if (scenario == "unreadable metadata") File.WriteAllText(Path.Combine(fixture.Paths.WorkshopDirectory, "123", "About", "About.xml"), "<invalid");
                var previewCount = 0;
                var applied = await ApplyAsync(window, manifest, null, default,
                    approvePreview: (_, notice, _) =>
                    {
                        previewCount++;
                        Require(!notice.Contains("前置") && !notice.Contains("诊断") && !notice.Contains("缺少"), "Import preview contains mod diagnostics");
                        Require(notice.Contains("合集声明游戏版本") == (scenario is "wrong version" or "old declared version"), "Only declared version differences should be reported");
                        return Task.FromResult(true);
                    });
                Require(previewCount == 1 && window.ViewModel.HasOrderStatus,
                    "Import failed or suppressed main-window diagnostics: " + scenario + "; " + window.ViewModel.Status);
                Require(window.ViewModel.Status.Contains("合集声明游戏版本") == (scenario is "wrong version" or "old declared version"), "Version warning was lost or invented");
                Require(!window.ViewModel.IsShowingCache, "Successful import kept the cache-only save restriction");
                Require(applied && window.ViewModel.Rows.Select(row => row.PackageId).SequenceEqual(manifest.Mods.Select(mod => mod.PackageId))
                    && !window.ViewModel.HasChanges, scenario + " blocked or altered import");
                using var savedInput = File.OpenRead(fixture.Config);
                Require(new ModsConfigXmlFormat().Read(savedInput).Order.PackageIds.SequenceEqual(manifest.Mods.Select(mod => mod.PackageId)), scenario + " was not saved");
                Require(window.ViewModel.Status.Contains("已导入并保存") && !window.ViewModel.Status.Contains("未应用"), scenario + " reported failure");
                checks.Add("Marketplace imports and saves unchanged author order despite " + scenario);
            }
            foreach (var scenario in new[] { "wait success", "wait cancel", "wait failure", "wait incomplete", "edit during wait" })
            {
                fixture = new Fixture(window, output, scenario, pathField);
                using var cancellation = new CancellationTokenSource();
                var entered = new TaskCompletionSource();
                var release = new TaskCompletionSource<IReadOnlyList<WorkshopItemState>>();
                var previewCount = 0;
                var operation = window.ApplyMarketplaceManifestAsync(fixture.Manifest, null, cancellation.Token,
                    approvePreview: (_, _, _) => { previewCount++; return Task.FromResult(true); },
                    download: async (_, _, token) => { entered.SetResult(); return await release.Task.WaitAsync(token); });
                await entered.Task;
                fixture.Unchanged();
                Require(previewCount == 0, "Import preview opened before downloads completed");
                if (scenario == "wait cancel") cancellation.Cancel();
                if (scenario == "edit during wait") window.ViewModel.MoveRowsTo([window.ViewModel.Rows[^1]], 0);
                var latest = window.ViewModel.Rows.Select(row => row.PackageId).ToArray();
                if (scenario == "wait failure") release.SetException(new IOException("合成 Steam 下载失败"));
                else release.SetResult(scenario == "wait incomplete" ? [] : Completed(fixture.Manifest));
                var applied = await operation;
                Require(applied == (scenario is "wait success" or "wait incomplete"), "Incorrect outcome after " + scenario);
                if (!applied)
                    Require(previewCount == 0 && File.ReadAllBytes(fixture.Config).SequenceEqual(fixture.OriginalBytes)
                        && window.ViewModel.Rows.Select(row => row.PackageId).SequenceEqual(latest), "Waiting overwrote original/later order");
                else Require(previewCount == 1 && window.ViewModel.Status.Contains("已导入并保存"), "Completion did not import once");
                if (scenario == "wait incomplete") Require(window.ViewModel.Status.Contains("尚未确认 3")
                    && window.ViewModel.Status.Contains("缺少 2") && !window.ViewModel.HasChanges,
                    "Incomplete download blocked import or lost missing-mod notice");
                checks.Add("Marketplace waits for Steam before import and protects original edits: " + scenario);
            }
            foreach (var invalid in new[] { "duplicate package", "invalid DLC declaration" })
            {
                fixture = new Fixture(window, output, invalid, pathField);
                var manifest = invalid == "duplicate package" ? fixture.Manifest with { Mods = [.. fixture.Manifest.Mods, fixture.Manifest.Mods[1]] }
                    : fixture.Manifest with { RequiredDlc = ["ludeon.rimworld.royalty"] };
                Require(!await ApplyAsync(window, manifest, null, default,
                    approvePreview: (_, _, _) => throw new InvalidOperationException("Malformed manifest reached preview")), "Malformed data was imported");
                fixture.Unchanged();
                checks.Add("Malformed marketplace data is rejected before any disk or editor change: " + invalid);
            }

            fixture = new Fixture(window, output, "no-op disk", pathField);
            fixture.Install(new("b.mod", "456")); fixture.Install(new("c.mod", "789"));
            var authoredFile = MarketplaceOrderCompatibility.ToOrderFile(fixture.Manifest);
            File.WriteAllBytes(fixture.Config, new ModsConfigXmlFormat().Write(authoredFile.Configuration));
            fixture.Capture();
            Require(await ApplyAsync(window, fixture.Manifest, null, default,
                approvePreview: (_, _, _) => Task.FromResult(true)), "No-op disk apply failed");
            Require(window.ViewModel.UndoCommand.CanExecute(null), "No-op disk import did not record combined history");
            window.ViewModel.UndoCommand.Execute(null); fixture.Unchanged(checkHistory: false);
            checks.Add("Marketplace unchanged-disk apply still records undoable editor replacement");
        }
        finally
        {
            pathField.SetValue(window, oldPaths);
            UserStorage.DirectoryPath = oldStorage;
            if (originalFile is not null) window.ViewModel.LoadFile(originalFile, originalLabel);
        }
    }

    private static IReadOnlyList<WorkshopItemState> Completed(MarketplaceManifest manifest) => manifest.Mods
        .Where(mod => mod.WorkshopId is not null).Select(mod => new WorkshopItemState(mod.WorkshopId!, true, true,
            Path.Combine(UserStorage.DirectoryPath, "synthetic-steam", mod.WorkshopId!))).ToArray();

    private static Task<bool> ApplyAsync(MainWindow window, MarketplaceManifest manifest, IProgress<string>? progress,
        CancellationToken token, Func<OrderComparison, string, CancellationToken, Task<bool>>? approvePreview = null) =>
        window.ApplyMarketplaceManifestAsync(manifest, progress, token, approvePreview,
            (_, _, _) => Task.FromResult(Completed(manifest)));

    private sealed class Fixture
    {
        private readonly MainWindow window;
        internal GamePaths Paths { get; }
        internal string Config => Path.Combine(Paths.ConfigDirectory, "ModsConfig.xml");
        internal MarketplaceManifest Manifest { get; } = new(1, "synthetic-order", "1.0.0", "市场 合成排序", "fixture",
            "Synthetic fixture only", "1.7", [], null, [new("ludeon.rimworld", null), new("a.mod", "123"), new("b.mod", "456"), new("c.mod", "789")]);
        internal byte[] OriginalBytes { get; private set; } = [];
        internal string[] OriginalOrder { get; private set; } = [];
        internal bool WasDirty { get; private set; }
        internal bool CouldUndo { get; private set; }
        internal bool CouldRedo { get; private set; }
        private string source = "";
        internal Fixture(MainWindow window, string output, string name, FieldInfo paths)
        {
            this.window = window;
            var root = Path.Combine(Path.GetFullPath(output), "marketplace-apply", name + "-" + Guid.NewGuid().ToString("N"));
            UserStorage.DirectoryPath = Path.Combine(root, "isolated-profile");
            Paths = new() { GameDirectory = Path.Combine(root, "游戏"), LocalModsDirectory = Path.Combine(root, "本地 Mods"),
                WorkshopDirectory = Path.Combine(root, "工坊"), ConfigDirectory = Path.Combine(root, "配置 Config") };
            foreach (var directory in new[] { Paths.GameDirectory, Paths.LocalModsDirectory, Paths.WorkshopDirectory, Paths.ConfigDirectory }) Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(Paths.GameDirectory, "Version.txt"), "1.7.4985 rev1269");
            WriteGameContent("Core", "ludeon.rimworld"); WriteMod("123", "a.mod"); WriteMod("999", "old.mod");
            var xml = new XDocument(new XElement("ModsConfigData", new XElement("version", "1.7.4985"),
                new XElement("activeMods", new XElement("li", "ludeon.rimworld"), new XElement("li", "old.mod"), new XElement("li", "a.mod")),
                new XElement("other", "keep-this-field")));
            xml.Save(Config);
            paths.SetValue(window, Paths);
            using var input = File.OpenRead(Config);
            window.ViewModel.LoadFile(new OrderFileCodec().Read(input), Config);
            window.ViewModel.ApplyScan(new ModScanner().Scan(Paths, targetOverride: new(1, 7), inspectContent: false), observeActivity: false);
            window.ViewModel.IsShowingCache = false;
            window.ViewModel.MoveRowsTo([window.ViewModel.Rows[^1]], 1);
            Capture();
        }
        internal void Capture()
        {
            OriginalBytes = File.ReadAllBytes(Config);
            OriginalOrder = window.ViewModel.Rows.Select(row => row.PackageId).ToArray();
            WasDirty = window.ViewModel.HasChanges; CouldUndo = window.ViewModel.UndoCommand.CanExecute(null); CouldRedo = window.ViewModel.RedoCommand.CanExecute(null);
            source = window.ViewModel.SourceLabel;
        }
        internal void Unchanged(bool checkHistory = true)
        {
            Require(File.ReadAllBytes(Config).SequenceEqual(OriginalBytes) && window.ViewModel.Rows.Select(row => row.PackageId).SequenceEqual(OriginalOrder)
                && window.ViewModel.SourceLabel == source && window.ViewModel.HasChanges == WasDirty, "Staging changed config/editor/source/dirty state");
            if (checkHistory) Require(window.ViewModel.UndoCommand.CanExecute(null) == CouldUndo && window.ViewModel.RedoCommand.CanExecute(null) == CouldRedo,
                "Staging changed history");
        }
        internal Task<ModScanResult> ScanAsync(CancellationToken token) => Task.FromResult(new ModScanner().Scan(Paths, token,
            targetOverride: new(1, 7), inspectContent: false));
        internal WorkshopItemState Install(ImportedModDownload item)
        {
            WriteMod(item.WorkshopId, item.Identifier, item.Identifier == "c.mod" ? "b.mod" : null);
            return new(item.WorkshopId, true, true, Path.Combine(Paths.WorkshopDirectory, item.WorkshopId));
        }
        internal void WriteMod(string workshop, string package, string? dependency = null) => WriteAbout(Path.Combine(Paths.WorkshopDirectory, workshop), package, dependency);
        internal void WriteGameContent(string folder, string package) => WriteAbout(Path.Combine(Paths.GameDirectory, "Data", folder), package, null);
        private static void WriteAbout(string directory, string package, string? dependency)
        {
            Directory.CreateDirectory(Path.Combine(directory, "About"));
            var about = new XElement("ModMetaData", new XElement("name", package), new XElement("author", "fixture"), new XElement("packageId", package),
                new XElement("supportedVersions", new XElement("li", "1.7")));
            if (dependency is not null) about.Add(new XElement("modDependencies", new XElement("li", new XElement("packageId", dependency), new XElement("displayName", dependency))));
            new XDocument(about).Save(Path.Combine(directory, "About", "About.xml"));
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
#endif
