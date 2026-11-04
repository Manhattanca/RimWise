#if DEBUG
using System.IO;
using System.Text;
using RimWise.Core;
using RimWise.Desktop.Services;
using RimWise.Formats;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

internal static class VersionCompatibilitySmokeCheck
{
    internal static async Task RunAsync(MainWindow owner, string output)
    {
        var root = Path.Combine(output, "版本 自动排序 中文 [test] &");
        var game = Path.Combine(root, "Game");
        var config = Path.Combine(root, "Config");
        var mods = Path.Combine(game, "Mods");
        Directory.CreateDirectory(config);
        foreach (var (directory, id) in new[] { (Path.Combine(game, "Data", "Core", "About"), "ludeon.rimworld"),
            (Path.Combine(mods, "TestMod", "About"), "test.mod") })
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "About.xml"), $"<ModMetaData><name>{id}</name><packageId>{id}</packageId><supportedVersions><li>1.7</li></supportedVersions></ModMetaData>");
        }
        File.WriteAllText(Path.Combine(game, "Version.txt"), "1.7.4995.1069");
        var configFile = Path.Combine(config, "ModsConfig.xml");
        File.WriteAllText(configFile, "<ModsConfigData><version>1.6.4633</version><activeMods><li>test.mod</li><li>ludeon.rimworld</li></activeMods><custom>保留内容</custom></ModsConfigData>");
        var original = File.ReadAllBytes(configFile);
        var paths = new GamePaths { GameDirectory = game, LocalModsDirectory = mods, ConfigDirectory = config, TargetVersionOverride = "1.6" };
        var scanner = new ModScanner();
        var metadata = scanner.Scan(paths, inspectContent: false);
        var window = new MainWindow(initializePaths: false) { Owner = owner };
        try
        {
            await window.FastStartupForCheckAsync(paths, (_, _, _) => Task.FromResult(metadata), (_, _) => Task.FromResult(metadata));
            Assert(window.ViewModel.CatalogueVersion?.Series == "1.6", "Fixture did not preserve the stale explicit override.");
            await window.PrepareAutomaticCatalogueAsync();
            var editor = window.ViewModel;
            Assert(editor.CatalogueVersion?.Series == "1.7" && editor.VersionLabel == "1.6.4633", "Automatic preparation did not use installed version without rewriting source.");
            var request = AiSortingSession.CreateRequest(editor, AiSettings.Default, null);
            Assert(request.GameVersion == "1.7.4995" && request.CurrentOrder.SequenceEqual(["test.mod", "ludeon.rimworld"]), "AI request used stale source or changed order.");
            var save = window.PrepareAutomaticOrderSave();
            Assert(File.ReadAllBytes(configFile).SequenceEqual(original), "Preparation wrote the game configuration.");
            var plan = AiOrderPlanner.PreparedOrder(request.CurrentOrder, ["ludeon.rimworld", "test.mod"], editor.CurrentCatalogue!, [], new GameVersion(1, 7, 4995), [], []);
            Assert(plan.CanApply, "Synthetic ordered candidate was rejected.");
            editor.ApplyAiPlan(plan, editor.ContentRevision);
            save();
            var saved = File.ReadAllBytes(configFile);
            using var stream = new MemoryStream(saved);
            var written = new ModsConfigXmlFormat().Read(stream);
            Assert(written.GameVersion == "1.7.4995" && written.Order.PackageIds.SequenceEqual(["ludeon.rimworld", "test.mod"])
                && Encoding.UTF8.GetString(saved).Contains("保留内容"), "Automatic final save lost order, version or unrelated content.");
            editor.UndoCommand.Execute(null);
            Assert(File.ReadAllBytes(configFile).SequenceEqual(original), "Undo did not restore original configuration bytes.");
            editor.RedoCommand.Execute(null);
            Assert(File.ReadAllBytes(configFile).SequenceEqual(saved), "Redo did not restore saved configuration bytes.");
            var nextSave = window.PrepareAutomaticOrderSave();
            File.WriteAllText(Path.Combine(game, "Version.txt"), "1.7.4996.1069");
            var buildRejected = false;
            try { nextSave(); } catch (IOException) { buildRejected = true; }
            Assert(buildRejected && File.ReadAllBytes(configFile).SequenceEqual(saved), "A changed runtime build bypassed final-save verification.");
            File.WriteAllText(Path.Combine(game, "Version.txt"), "1.6.4633");
            var rejected = false;
            try { nextSave(); } catch (IOException) { rejected = true; }
            Assert(rejected && File.ReadAllBytes(configFile).SequenceEqual(saved), "A changed runtime version bypassed final-save verification.");
        }
        finally { window.ViewModel.MarkExported(); window.Close(); }
        File.WriteAllText(Path.Combine(output, "version-compatibility.txt"), "Passed: four-part installed version; stale explicit override; source 1.6 configuration; installed-version catalogue/request/plan; deferred final save; byte-exact undo/redo; changed runtime rejection. No game or AI started.");
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
#endif
