#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Globalization;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

internal static class AvailableSortingSmokeCheck
{
    internal static async Task RunAsync(MainWindow window, string output)
    {
        var vm = window.ViewModel;
        var fixture = Path.Combine(output, "未启用排序 中文 [样例] & 库");
        ModInstallation Mod(string name, string id, string author) => new(Path.Combine(fixture, id), id,
            ModSource.Local, "", new(name, author, id, ["1.7"], "", ModRequirements.Empty, false), []);
        var mods = new[] { Mod("Bravo", "sort.c", "Alpha"), Mod("alpha", "sort.b", "Zulu"),
            Mod("Charlie", "sort.a", ""), Mod("中文模组", "sort.d", "中文作者") };
        var scan = new ModScanResult(new(null, "", ""), GameVersion.Parse("1.7"), new(mods), []);
        vm.ApplyScan(scan);
        var enabled = vm.Rows.Select(row => row.PackageId).ToArray();
        var revision = vm.ContentRevision;
        var dirty = vm.HasChanges;
        var undo = vm.UndoCommand.CanExecute(null);
        var action = vm.LastAction;
        string[] Visible() => vm.VisibleAvailableMods.Cast<InstalledModRow>().Select(row => row.PackageId).ToArray();
        async Task Layout() => await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);

        foreach (var field in new[] { AvailableModSortField.Name, AvailableModSortField.PackageId, AvailableModSortField.Author })
        {
            vm.SetAvailableSortField(field);
            var ascending = Visible();
            var comparer = new AvailableModComparer(field, false);
            Assert(vm.VisibleAvailableMods.Cast<InstalledModRow>().Zip(vm.VisibleAvailableMods.Cast<InstalledModRow>().Skip(1))
                .All(pair => comparer.Compare(pair.First, pair.Second) <= 0), "Ascending view is not sorted.");
            vm.ToggleAvailableSortDirection();
            var descending = Visible();
            if (field == AvailableModSortField.Author)
                Assert(ascending[^1] == "sort.a" && descending[^1] == "sort.a"
                    && ascending[..^1].Reverse().SequenceEqual(descending[..^1]), "Missing author or reverse order failed.");
            else Assert(ascending.Reverse().SequenceEqual(descending), "Reverse order failed.");
            vm.ToggleAvailableSortDirection();
        }
        vm.SetAvailableSortField(AvailableModSortField.PackageId);
        Assert(Visible().SequenceEqual(new[] { "sort.a", "sort.b", "sort.c", "sort.d" }), "Package ID order incorrect.");
        var selected = vm.AvailableMods.Where(row => row.PackageId is "sort.a" or "sort.c").ToArray();
        vm.SelectAvailableRows(selected, selected[0]);
        await Layout();
        vm.ToggleAvailableSortDirection();
        await Layout();
        Assert(vm.SelectedAvailableRows.ToHashSet().SetEquals(selected) && window.AvailableGrid.SelectedItems.Count == 2,
            "Sorting lost multi-selection.");
        var expected = Visible();
        vm.SearchText = "Bravo";
        Assert(Visible().SequenceEqual(new[] { "sort.c" }), "Search failed.");
        vm.SearchText = "";
        Assert(Visible().SequenceEqual(expected), "Search reset the display order.");
        vm.ApplyScan(scan);
        Assert(vm.AvailableSortField == AvailableModSortField.PackageId && vm.IsAvailableSortDescending
            && Visible().SequenceEqual(expected), "Refresh reset the display order.");
        Assert(vm.Rows.Select(row => row.PackageId).SequenceEqual(enabled) && vm.ContentRevision == revision
            && vm.HasChanges == dirty && vm.UndoCommand.CanExecute(null) == undo && vm.LastAction == action,
            "Display sorting changed the enabled order or history.");
        var count = vm.Rows.Count;
        Assert(vm.AddAvailableRows(vm.AvailableMods.Reverse(), count), "Batch append failed.");
        Assert(vm.Rows.Skip(count).Select(row => row.PackageId).SequenceEqual(expected), "Batch append ignored visible order.");
        vm.UndoCommand.Execute(null);
        Assert(vm.Rows.Select(row => row.PackageId).SequenceEqual(enabled), "Batch append was not one undo step.");
        vm.SetAvailableSortField(AvailableModSortField.Name);
        vm.ToggleAvailableSortDirection();
        vm.MarkExported();
        CheckPinyin();
        CheckFormats();
        await RunActivityAsync(window, fixture);
        File.WriteAllText(Path.Combine(output, "available-sorting.json"), "{\"Passed\":true}");
    }

    private static void CheckFormats()
    {
        ModInstallation Mod(string name, ModCodeKind kind, ModContentKinds contents = ModContentKinds.None) => new(name, name,
            ModSource.Local, "", new(name, "", name, [], "", ModRequirements.Empty, false), [])
            { CodeKind = kind, ContentKinds = contents };
        var rows = new[] { Mod("中文", ModCodeKind.Xml), Mod("Zebra", ModCodeKind.Xml), Mod("apple", ModCodeKind.CSharp),
            Mod("未知", ModCodeKind.Unknown), Mod("翻译", ModCodeKind.Xml, ModContentKinds.Translation),
            Mod("资源", ModCodeKind.Xml, ModContentKinds.Textures),
            Mod("混合", ModCodeKind.Xml, ModContentKinds.Translation | ModContentKinds.Audio) }
            .Select(mod => new InstalledModRow(mod, false)).ToArray();
        Assert(rows.OrderBy(row => row, new AvailableModComparer(AvailableModSortField.ModFormat, false)).Select(row => row.Name)
            .SequenceEqual(new[] { "apple", "Zebra", "中文", "翻译", "资源", "混合", "未知" }), "Ascending format groups failed.");
        Assert(rows.OrderBy(row => row, new AvailableModComparer(AvailableModSortField.ModFormat, true)).Select(row => row.Name)
            .SequenceEqual(new[] { "混合", "资源", "翻译", "Zebra", "中文", "apple", "未知" }), "Descending format groups or name ties failed.");
    }

    private static void CheckPinyin()
    {
        ModInstallation Mod(string name) => new(name, name, ModSource.Local, "",
            new(name, "", name, [], "", ModRequirements.Empty, false), []);
        var names = new[] { "中文", "Zebra", "白猫", "apple", "重庆", "banana", "阿尔法", "长剑", "柏木" };
        var expected = new[] { "阿尔法", "apple", "白猫", "柏木", "banana", "长剑", "重庆", "Zebra", "中文" };
        var rows = names.Select(name => new InstalledModRow(Mod(name), false)).ToArray();
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var sorted = rows.OrderBy(row => row, new AvailableModComparer(AvailableModSortField.Name, false)).Select(row => row.Name);
                Assert(sorted.SequenceEqual(expected), "Mixed pinyin order incorrect for " + culture + ": " + string.Join(", ", sorted));
                Assert(rows.OrderBy(row => row, new AvailableModComparer(AvailableModSortField.Name, true))
                    .Select(row => row.Name).SequenceEqual(expected.Reverse()), "Descending pinyin order incorrect.");
                Assert(rows.OrderBy(row => row, new AvailableModComparer(AvailableModSortField.RecentlyAdded, true))
                    .Select(row => row.Name).SequenceEqual(expected), "Unknown dates did not fall back to pinyin order.");
            }
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        Assert(PinyinSortKey.Create("重庆银行重新播放音乐") == "chongqingyinhangchongxinbofangyinyue", "Phrase pronunciation failed.");
        Assert(PinyinSortKey.Create("綠色建築") == "lvsejianzhu", "Traditional Chinese or ü conversion failed.");
        Assert(PinyinSortKey.Create("𠀀") == "he", "Supplementary-plane Chinese was split into UTF-16 halves.");
        Assert(PinyinSortKey.Create("Ａlpha") == "Alpha", "Full-width Latin normalization failed.");
        Assert(PinyinSortKey.Create("mod😀 & [1.7]") == "mod😀 & [1.7]", "Non-Chinese symbols were lost.");
    }

    private static async Task RunActivityAsync(MainWindow window, string fixture)
    {
        var vm = window.ViewModel;
        var day = DateTimeOffset.UtcNow.AddDays(-4);
        ModInstallation Mod(string name, string id, DateTimeOffset? updated) => new(Path.Combine(fixture, "活动", id), id,
            id == "recent.c" ? ModSource.Workshop : ModSource.Local, "", new(name, "作者", id, ["1.7"], "", ModRequirements.Empty, false), [])
            { LastUpdatedUtc = updated, CodeKind = id == "recent.c" ? ModCodeKind.CSharp : ModCodeKind.Xml,
                ContentKinds = id switch { "recent.b" => ModContentKinds.Textures | ModContentKinds.Audio,
                    "recent.a" => ModContentKinds.Translation, "recent.c" => ModContentKinds.ManagedCode | ModContentKinds.Definitions,
                    _ => ModContentKinds.Definitions | ModContentKinds.Patches } };
        var mods = new[] { Mod("白猫", "recent.c", day.AddDays(3)), Mod("apple", "recent.b", day.AddDays(1)),
            Mod("长剑", "recent.a", day.AddDays(2)), Mod("中文模组", "recent.d", null) };
        var scan = new ModScanResult(new(null, "", ""), GameVersion.Parse("1.7"), new(mods), []);
        vm.ApplyScan(scan);
        var rows = vm.AvailableMods.ToDictionary(row => row.PackageId);
        rows["recent.c"].SetActivity(true, true, day, day.AddDays(3));
        rows["recent.b"].SetActivity(true, false, day.AddDays(2), day.AddDays(1));
        var enabled = vm.Rows.Select(row => row.PackageId).ToArray();
        var revision = vm.ContentRevision;
        string[] Visible() => vm.VisibleAvailableMods.Cast<InstalledModRow>().Select(row => row.PackageId).ToArray();
        window.AvailableSortBox.SelectedIndex = 3;
        Assert(Visible().SequenceEqual(new[] { "recent.b", "recent.c", "recent.a", "recent.d" }),
            $"Recent additions incorrect ({vm.AvailableSortField}, descending={vm.IsAvailableSortDescending}): " +
            string.Join(", ", vm.VisibleAvailableMods.Cast<InstalledModRow>().Select(row => $"{row.PackageId}={row.AddedUtc:o}")));
        window.AvailableSortDirectionButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert(Visible().SequenceEqual(new[] { "recent.c", "recent.b", "recent.a", "recent.d" }), "Ascending additions or unknown fallback failed.");
        window.AvailableSortBox.SelectedIndex = 4;
        Assert(Visible().SequenceEqual(new[] { "recent.c", "recent.a", "recent.b", "recent.d" }), "Update order used addition time or folder/name order.");
        window.AvailableSortDirectionButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert(Visible().SequenceEqual(new[] { "recent.b", "recent.a", "recent.c", "recent.d" }), "Ascending updates failed.");
        vm.ToggleAvailableSortDirection();
        vm.ApplyScan(scan);
        Assert(Visible().SequenceEqual(new[] { "recent.c", "recent.a", "recent.b", "recent.d" }), "Refresh lost update dates or sort direction.");
        Assert(vm.Rows.Select(row => row.PackageId).SequenceEqual(enabled) && vm.ContentRevision == revision, "Time sorting edited enabled order.");
        // Leave both distinct vector icons visible for the light/dark/minimum-width captures.
        var first = vm.AvailableMods.Single(row => row.PackageId == "recent.c");
        first.SetActivity(true, true, day, day.AddDays(3));
        Assert(first.AddedTooltip.Contains("首次发现") && first.UpdatedTooltip.Contains("最近更新"), "Activity tooltip dates missing.");
        window.AvailableSortBox.SelectedIndex = 0;
        if (vm.IsAvailableSortDescending) vm.ToggleAvailableSortDirection();
        Assert(Visible().SequenceEqual(new[] { "recent.b", "recent.c", "recent.a", "recent.d" }), "Pinyin view order incorrect.");
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
#endif
