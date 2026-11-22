namespace RimWise.Integration.Diagnostics;

internal static class DiagnosticCoverage
{
    internal static object Create(DateTimeOffset started, AutomaticLogRun? run, HashSet<string> locations,
        HashSet<string> exported, int exclusions)
    {
        // Use only the exporter inventory: never reopen live files or follow links to
        // make stronger claims about a later/different filesystem state.
        var directories = new HashSet<string>(locations, StringComparer.OrdinalIgnoreCase);
        if (run is not null)
            foreach (var session in run.Sessions) directories.Add("Data/GameTests/" + session);
        return new
        {
            format = "RimWiseDiagnosticCoverage/1", snapshotStartedUtc = started, snapshotEndedUtc = DateTimeOffset.UtcNow,
            scope = run is null ? "all-retained-evidence" : "selected-run", selectedRun = run,
            consistency = "per-file-snapshot-not-transactional",
            missingMeaning = "未在本包找到；可能未生成、已清理、旧版不支持、不适用或安全排除，不能据此判定故障或成功。",
            terminalMeaning = "阶段完成不代表游戏测试成功；无终态时原因未知，不能认定正常完成。",
            exclusionCount = exclusions,
            evidence = exported.Order(StringComparer.Ordinal).ToArray(),
            locations = directories.Order(StringComparer.Ordinal).Select(directory => new
            {
                directory, observed = locations.Contains(directory),
                files = (Path.GetFileName(directory).StartsWith("attempt-", StringComparison.OrdinalIgnoreCase)
                    ? GameEvidenceCatalog.AttemptFiles : GameEvidenceCatalog.SessionFiles).Select(name => new
                    {
                        name, status = exported.Contains(directory + "/" + name) ? "exported" : "not-found-in-export-snapshot"
                    }).ToArray()
            }).ToArray()
        };
    }
}
