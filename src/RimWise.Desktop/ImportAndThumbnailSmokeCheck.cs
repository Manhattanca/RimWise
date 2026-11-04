#if DEBUG
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Services;
using RimWise.Formats;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;
using RimWise.Integration.Steam;

namespace RimWise.Desktop;

internal static class ImportAndThumbnailSmokeCheck
{
    internal static async Task RunAsync(MainWindow window, string output, List<string> checks)
    {
        var root = Path.Combine(output, "拖入下载 中文 [测试] &"); Directory.CreateDirectory(root);
        var vm = window.ViewModel;
        ModInstallation Mod(string id, string workshop, ModSource source = ModSource.Workshop) =>
            new(Path.Combine(root, id), id, source, workshop, new(id, "fixture", id, ["1.7"], "", ModRequirements.Empty, false), [])
            { CodeKind = ModCodeKind.Xml };
        var a = Mod("a.mod", "123"); var b = Mod("b.mod", "456"); var c = Mod("c.mod", "", ModSource.Local);
        foreach (var mod in new[] { a, b, c }) Directory.CreateDirectory(Path.Combine(mod.Directory, "About"));
        var pixels = Enumerable.Repeat((byte)120, 480 * 180 * 3).ToArray();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(480, 180, 96, 96, PixelFormats.Bgr24, null, pixels, 480 * 3)));
        using (var stream = File.Create(Path.Combine(a.Directory, "About", "Preview.png"))) encoder.Save(stream);
        File.Copy(Path.Combine(a.Directory, "About", "Preview.png"), Path.Combine(c.Directory, "About", "Preview.png"));
        File.WriteAllText(Path.Combine(b.Directory, "About", "Preview.png"), "malformed preview");
        ModScanResult Scan(params ModInstallation[] mods) => new(new(null, "", ""), new(1, 7), new(mods), []);
        var imported = Path.Combine(root, "当前 存档.rws");
        File.WriteAllText(imported, "<savegame><meta><gameVersion>1.7</gameVersion><modIds><li>a.mod</li><li>b.mod</li></modIds>"
            + "<modSteamIds><li>123</li><li>456</li></modSteamIds></meta><game/></savegame>");
        var sourceBytes = File.ReadAllBytes(imported);
        var entered = new TaskCompletionSource();
        var finish = new TaskCompletionSource<IReadOnlyList<WorkshopItemState>>();
        var scans = 0;
        vm.IsShowingCache = true;
        var task = window.ImportOrderAsync(imported, async (plan, progress, token) =>
        {
            Assert(plan.Downloads.SequenceEqual([new ImportedModDownload("b.mod", "456")]), "Installed mod was downloaded again");
            entered.SetResult(); return await finish.Task.WaitAsync(token);
        }, _ => Task.FromResult(++scans == 1 ? Scan(a, c) : Scan(a, b, c)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(!window.ShouldReadStartupOrder, "Startup would replace an imported document with game order");
        Assert(window.CancelImportDownloadButton.IsVisible, "Missing cancel download control");
        vm.MoveRowsTo([vm.Rows[1]], 0);
        var revision = vm.ContentRevision; var action = vm.LastAction;
        finish.SetResult([new("456", true, true, b.Directory)]);
        await task;
        Assert(vm.Rows.Select(row => row.PackageId).SequenceEqual(["b.mod", "a.mod"])
            && vm.ContentRevision == revision && vm.LastAction == action && vm.Rows.All(row => row.Installation is not null),
            "Download refresh lost current edits or did not match installation");
        vm.UndoCommand.Execute(null);
        Assert(vm.Rows.Select(row => row.PackageId).SequenceEqual(["a.mod", "b.mod"]), "Download refresh consumed undo history");
        Assert(sourceBytes.SequenceEqual(File.ReadAllBytes(imported)), "Import changed source save");
        Assert(scans == 2, "Download performed another full scan after merging the completed items");
        checks.Add("Imported save missing-only download merges once and preserves intervening edits and undo");

        var availableRow = vm.AvailableMods.Single(row => row.PackageId == "c.mod");
        var untouchedInstallation = vm.Rows.Single(row => row.PackageId == "a.mod").Installation;
        vm.SelectedAvailable = availableRow;
        var beforeDeltaRevision = vm.ContentRevision;
        var updated = b with { Metadata = b.Metadata! with { Name = "B updated" } };
        vm.MergeWorkshopInstallations([updated], new());
        Assert(ReferenceEquals(vm.SelectedAvailable, availableRow) && vm.AvailableMods.Contains(availableRow)
            && ReferenceEquals(vm.Rows.Single(row => row.PackageId == "a.mod").Installation, untouchedInstallation)
            && vm.Rows.Single(row => row.PackageId == "b.mod").Name == "B updated" && vm.ContentRevision == beforeDeltaRevision,
            "Incremental merge reset an unrelated installation, selection or edit state");
        checks.Add("Download delta preserves unrelated row identity and selection while updating only changed metadata");

        // Exercise the actual external routed file drop over a child grid, with all entries installed (no network).
        var args = Drag(new DataObject(DataFormats.FileDrop, new[] { imported }), DragDropEffects.Copy, window.OrderGrid);
        args.RoutedEvent = DragDrop.PreviewDropEvent;
        var oldDocument = vm.DocumentRevision;
        window.OrderGrid.RaiseEvent(args);
        for (var attempt = 0; attempt < 200 && (vm.DocumentRevision == oldDocument || !vm.Status.StartsWith("已导入排序")); attempt++)
            await Task.Delay(25);
        Assert(args.Handled && args.Effects == DragDropEffects.Copy && vm.DocumentRevision > oldDocument
            && vm.Status.StartsWith("已导入排序"), "External file drop did not import directly");
        var internalDrag = Drag(new DataObject("RimWise.synthetic", "row"), DragDropEffects.Move, window);
        internalDrag.RoutedEvent = DragDrop.PreviewDragOverEvent;
        window.RaiseEvent(internalDrag);
        Assert(!internalDrag.Handled, "File handler intercepted internal row drag");
        var rejected = Drag(new DataObject(DataFormats.FileDrop, new[] { imported, imported }), DragDropEffects.Copy, window.OrderGrid);
        rejected.RoutedEvent = DragDrop.PreviewDropEvent; oldDocument = vm.DocumentRevision;
        window.OrderGrid.RaiseEvent(rejected);
        Assert(rejected.Handled && rejected.Effects == DragDropEffects.None && vm.DocumentRevision == oldDocument, "Multi-file drop replaced order");
        checks.Add("External drop routes through child grid; multiple files rejected; internal row drag unaffected");

        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            ((App)Application.Current).Themes.Select(theme, save: false);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            foreach (var grid in new[] { window.AvailableGrid, window.OrderGrid })
            {
                var thumbnails = Descendants(grid).OfType<ModThumbnail>().Where(t => t.IsVisible).ToArray();
                Assert(thumbnails.Length > 0, "Missing thumbnails in " + grid.Name);
                await Task.WhenAll(thumbnails.Select(t => t.ImageLoading));
                foreach (var thumbnail in thumbnails)
                {
                    Assert(!ToolTipService.GetIsEnabled(thumbnail) && thumbnail.ToolTip is "", "Thumbnail tooltip was enabled");
                    Assert(ReferenceEquals(thumbnail.InputHitTest(new Point(15, 15)), thumbnail), "Thumbnail hover falls through to row");
                    var parent = (StackPanel)VisualTreeHelper.GetParent(thumbnail);
                    Assert(ReferenceEquals(parent.Children[0], thumbnail), "Cover appears after source badge");
                    var image = ((Image)thumbnail.Child).Source as BitmapSource;
                    if (thumbnail.Installation?.PackageId is "a.mod" or "c.mod")
                        Assert(image is { IsFrozen: true, PixelWidth: <= 64, PixelHeight: <= 64 }, "Cover missing or decoded without bounds");
                    else Assert(image is null, "Invalid image did not degrade safely");
                }
            }
            PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, theme + "-行首缩略图.png"));
        }
        var recycled = Descendants(window.OrderGrid).OfType<ModThumbnail>().First();
        recycled.Installation = a; var previous = recycled.ImageLoading;
        recycled.Installation = b; await previous; await recycled.ImageLoading;
        Assert(((Image)recycled.Child).Source is null, "Recycled cell retained stale image");
        using (var exclusive = new FileStream(Path.Combine(a.Directory, "About", "Preview.png"), FileMode.Open, FileAccess.Read, FileShare.None)) { }
        checks.Add("Day/night both lists have bounded lazy covers before badges with no tooltip, invalid and recycled images safe");

        // Superseding the imported document must cancel its wait and must never restore its old order.
        entered = new(); finish = new(); scans = 0;
        task = window.ImportOrderAsync(imported, async (_, _, token) => { entered.SetResult(); return await finish.Task.WaitAsync(token); },
            _ => Task.FromResult(Scan(a, c)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        window.LoadSample(); var replacement = vm.Rows.Select(row => row.PackageId).ToArray();
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(vm.Rows.Select(row => row.PackageId).SequenceEqual(replacement) && !window.CancelImportDownloadButton.IsVisible,
            "Superseded download overwrote another document or remained running");
        checks.Add("Replacing document cancels stale import wait without replacing latest order");

        // Explicit cancellation is independent from the editor and does not clear the imported order.
        entered = new();
        task = window.ImportOrderAsync(imported, async (_, _, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return []; }, _ => Task.FromResult(Scan(a)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        window.CancelImportDownloadButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(vm.Rows.Select(row => row.PackageId).SequenceEqual(["a.mod", "b.mod"]) && vm.Status.Contains("已停止等待"), "Cancellation erased order");
        checks.Add("Cancel Steam wait leaves imported entries and communicates pending Steam work");
        vm.MarkExported();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static DragEventArgs Drag(IDataObject data, DragDropEffects effects, DependencyObject target) =>
        (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [data, DragDropKeyStates.None, effects, target, new Point(1, 1)], null)!;
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
#endif
