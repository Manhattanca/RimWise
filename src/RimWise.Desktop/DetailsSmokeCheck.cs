#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using RimWise.Desktop.Services;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class DetailsSmokeCheck
{
    internal static void CreateFixtures(GamePaths paths)
    {
        var about = Path.Combine(paths.LocalModsDirectory, "建筑", "About");
        File.WriteAllText(Path.Combine(about, "PublishedFileId.txt"), "123456789");
        // A generated fixture is confined to ignored work/, never shipped as a mod asset.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 66, 72)), null, new Rect(0, 0, 480, 180));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(75, 135, 120)), null, new Rect(0, 135, 480, 45));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(196, 213, 193)), null, new Rect(170, 65, 140, 90));
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(144, 176, 159)), null,
                Geometry.Parse("M 150,70 L 240,15 L 330,70 Z"));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(45, 87, 85)), null, new Rect(222, 105, 36, 50));
        }
        var image = new RenderTargetBitmap(480, 180, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        Save(image, Path.Combine(about, "Preview.png"));
        File.WriteAllText(Path.Combine(paths.LocalModsDirectory, "闲置武器", "About", "Preview.png"), "invalid preview");
        var huge = BitmapSource.Create(9000, 1, 96, 96, PixelFormats.Bgr24, null, new byte[27000], 27000);
        Save(huge, Path.Combine(paths.LocalModsDirectory, "闲置建筑", "About", "Preview.png"));
    }

    internal static async Task RunAsync(MainWindow window, GamePaths paths, string output)
    {
        var vm = window.ViewModel;
        var building = vm.Rows.Single(row => row.PackageId == "example.buildings");
        var framework = vm.Rows.Single(row => row.PackageId == "example.framework");
        vm.SelectedRow = building;
        await vm.PreviewLoading;
        if (vm.SelectedPreview is not BitmapSource { PixelWidth: 480, PixelHeight: 180 }
            || vm.SelectedDirectory != Path.Combine(paths.LocalModsDirectory, "建筑")
            || !vm.HasSelectedWorkshopLink || !vm.SelectedWorkshopLink.EndsWith("id=123456789"))
            throw new InvalidOperationException("Preview, local mod path or PublishedFileId workshop link did not load.");

        vm.SelectedRow = framework;
        await vm.PreviewLoading;
        if (vm.HasSelectedPreview || !vm.SelectedWorkshopLink.EndsWith("id=100"))
            throw new InvalidOperationException("Selecting a mod without a preview retained the previous image.");
        vm.SelectedRow = building;
        var pending = vm.PreviewLoading;
        vm.SelectedRow = framework;
        await pending;
        await vm.PreviewLoading;
        if (vm.HasSelectedPreview) throw new InvalidOperationException("A stale preview replaced a newer selection.");
        foreach (var id in new[] { "inactive.weapons", "inactive.buildings" })
        {
            vm.SelectedAvailable = vm.AvailableMods.Single(row => row.PackageId == id);
            await vm.PreviewLoading;
            if (vm.HasSelectedPreview || vm.HasSelectedWorkshopLink)
                throw new InvalidOperationException("Invalid/oversized image or absent workshop ID was not handled.");
        }
        vm.SelectedRow = building;
        await vm.PreviewLoading;
        window.UpdateLayout();
        if (window.ModPreview.ActualWidth <= 0 || window.ModPreview.ActualHeight <= 0)
            throw new InvalidOperationException("Local preview was not rendered.");
        PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, "本地预览图与工坊链接.png"));
        // OnLoad must release the source file rather than holding a game/mod file lock.
        using var exclusive = new FileStream(Path.Combine(paths.LocalModsDirectory, "建筑", "About", "Preview.png"),
            FileMode.Open, FileAccess.Read, FileShare.None);
        CheckLocationOpening(vm.SelectedWorkshopUri!, vm.SelectedDirectory, output);
    }

    private static void CheckLocationOpening(Uri workshop, string directory, string output)
    {
        var attempts = new List<string>();
        var opener = new ModLocationOpener(attempts.Add, () => true);
        if (opener.OpenWorkshop(workshop) != WorkshopOpenResult.Steam
            || !attempts.SequenceEqual(["steam://url/CommunityFilePage/123456789"]))
            throw new InvalidOperationException("Workshop link did not prefer Steam or also opened the browser.");
        attempts.Clear();
        opener = new ModLocationOpener(target =>
        {
            attempts.Add(target);
            if (target.StartsWith("steam:", StringComparison.Ordinal)) throw new Win32Exception(1155);
        }, () => true);
        if (opener.OpenWorkshop(workshop) != WorkshopOpenResult.Browser
            || !attempts.SequenceEqual(["steam://url/CommunityFilePage/123456789", workshop.AbsoluteUri]))
            throw new InvalidOperationException("Steam dispatch failure did not fall back to the browser once.");
        attempts.Clear();
        opener = new ModLocationOpener(attempts.Add, () => false);
        if (opener.OpenWorkshop(workshop) != WorkshopOpenResult.Browser || !attempts.SequenceEqual([workshop.AbsoluteUri]))
            throw new InvalidOperationException("Missing Steam protocol did not use browser directly.");
        attempts.Clear();
        if (opener.OpenWorkshop(new Uri("https://example.com/?id=123")) != WorkshopOpenResult.Failed
            || opener.OpenWorkshop(new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=123&x=bad")) != WorkshopOpenResult.Failed
            || attempts.Count != 0) throw new InvalidOperationException("An arbitrary workshop navigation target was dispatched.");
        if (!opener.OpenDirectory(directory) || !attempts.SequenceEqual([directory]))
            throw new InvalidOperationException("Unicode mod directory was not opened as one intact path.");
        attempts.Clear();
        if (opener.OpenDirectory(workshop.AbsoluteUri) || opener.OpenDirectory(Path.Combine(output, "不存在目录"))
            || attempts.Count != 0) throw new InvalidOperationException("An invalid mod directory was dispatched.");
        opener = new ModLocationOpener(_ => throw new Win32Exception(2), () => true);
        if (opener.OpenWorkshop(workshop) != WorkshopOpenResult.Failed || opener.OpenDirectory(directory))
            throw new InvalidOperationException("Failed shell navigation was reported as successful.");
        File.WriteAllText(Path.Combine(output, "location-opening.json"), "{\"Passed\":true,\"Checks\":7,\"RealSteamOrExplorerLaunch\":false}");
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
#endif
