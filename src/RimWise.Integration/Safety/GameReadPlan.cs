namespace RimWise.Integration.Safety;

internal sealed record GameReadPlan(string Game, string Executable, string[] ModDirectories,
    string[] ReadObjects, string[] ExcludedRootFiles, string[]? RuntimeReadFiles = null);

/// <summary>Allowlist of existing installation objects. No recursive game-root grant is produced.</summary>
internal static class GameReadPlanner
{
    internal static GameReadPlan Create(string game, IEnumerable<string> modDirectories, string[]? runtimeReadFiles = null,
        Action<string>? progress = null, CancellationToken cancellation = default)
    {
        game = ScopedPaths.Ordinary(game, true);
        var executable = ScopedPaths.Child(game, "RimWorld.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("未找到 RimWorld.exe。", executable);
        var mods = modDirectories.Select(p => ScopedPaths.Ordinary(p, true))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (mods.Length is 0 or > 32) throw new InvalidDataException("测试输入数量无效。");
        var objects = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { game };
        var excluded = new List<string>();
        using var batch = OperatingSystem.IsWindows() ? new PinnedDirectorySet() : null;
        batch?.PinDirectory(game);
        var reporting = new GameTestProgress("正在建立读取清单", 0, progress);
        reporting.Update(0);
        // Explicit trusted-host compatibility input, never a path supplied by the model.
        // It remains outside the default plan and requires a newly approved read plan.
        var extra = runtimeReadFiles?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (extra is not null)
            foreach (var name in extra)
            {
                if (name != "Juij_Steam.json" || !File.Exists(Path.Combine(game, "Juij_Steam.dll")))
                    throw new InvalidDataException("未允许的启动组件读取对象。");
                var path = ScopedPaths.Child(game, name);
                if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
                    throw new InvalidDataException("启动组件配置缺失或超出读取上限。");
            }
        foreach (var file in Directory.EnumerateFiles(game))
        {
            cancellation.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var permitted = name.Equals("RimWorld.exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(name).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || name is "RimWorld.deps.json" or "RimWorld.runtimeconfig.json" or "Assets.zip"
                    or "Version.txt" or "steam_appid.txt"
                || extra?.Contains(name, StringComparer.OrdinalIgnoreCase) == true;
            if (permitted) Add(file); else excluded.Add(ScopedPaths.Ordinary(file, true));
        }
        // Only these resource roots; never Logs, Crashes, MyBuildings, or the whole Mods tree.
        foreach (var name in new[] { "Data", "Rendering", "vulkan", "RimWorldWin64_Data", "MonoBleedingEdge" })
        {
            var path = ScopedPaths.Child(game, name);
            if (Directory.Exists(path)) Tree(path);
        }
        foreach (var mod in mods) Tree(mod);
        // Listing the two mod containers is required to discover the selected installations.
        // These directory-only ACEs do not grant access to unselected children's contents.
        foreach (var parent in mods.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)) Add(parent);
        var localMods = ScopedPaths.Child(game, "Mods");
        if (Directory.Exists(localMods)) Add(localMods);
        progress?.Invoke($"读取清单已完成：{objects.Count} 个对象");
        return new(game, executable, mods, objects.Order(StringComparer.OrdinalIgnoreCase).ToArray(), excluded.ToArray(), extra);

        void Add(string path)
        {
            cancellation.ThrowIfCancellationRequested();
            if (batch is null) path = ScopedPaths.Ordinary(path, true);
            else
            {
                path = ScopedPaths.Normalize(path);
                batch.PinParent(path);
            }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("测试输入不能包含链接或重解析点。");
            if (Path.GetFileName(path).Equals("PhinixCredentials.bin", StringComparison.OrdinalIgnoreCase))
                throw new IOException("凭据文件不能成为隔离读取输入。");
            objects.Add(path);
            if (objects.Count > 100000) throw new IOException("测试输入过多，停止本次可行性测试。");
            reporting.Update(objects.Count);
        }
        void Tree(string root)
        {
            // Validate before enumerating each level; do not traverse a junction during planning.
            Add(root);
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                batch?.PinDirectory(directory);
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    Add(entry);
                    if (Directory.Exists(entry)) pending.Push(entry);
                }
            }
        }
    }
}
