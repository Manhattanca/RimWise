using System.Text.RegularExpressions;

namespace RimWise.Integration.Diagnostics;

/// <summary>Export permission is not permission to compress or delete a recovery journal.</summary>
internal static partial class GameEvidenceCatalog
{
    internal static readonly string[] SessionFiles =
    [
        "session.json", "catalogue-snapshot.json", "preparation.json", "finished.json", "recovered.json",
        "helper-deployment.json", "helper-mount.json", "helper-cleanup.json"
    ];

    internal static readonly string[] AttemptFiles =
    [
        "attempt-lifecycle.jsonl", "runtime-Player.log", "Player.log", "output_log.txt", "stdout.log", "stderr.log",
        "launch.json", "launch-failure.log", "process.json", "result.json", "recovered.json", "log-source.json",
        "steam-request.json", "steam-observation.jsonl", "steam-result.json",
        "diagnostic-sources.json", "diagnostic-context.json", "ModsConfig.xml",
        "intended-ModsConfig.xml", "observed-ModsConfig.xml",
        "workbench-helper-failure.txt", "workbench-progress.xml", "workbench-signal.xml", "workbench-probe.xml",
        "workbench-messages.log", "workbench-factions.txt", "workbench-startup.xml",
        "workbench-log-locations.xml", "workbench-loaded-order.xml"
    ];

    private static readonly HashSet<string> ExportFiles = new(SessionFiles.Concat(AttemptFiles), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RecoveryFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "session.json", "launch.json", "process.json", "finished.json", "recovered.json",
        "helper-deployment.json", "helper-mount.json", "helper-cleanup.json", "ModsConfig.xml",
        "workbench-probe.xml", "workbench-signal.xml", "workbench-progress.xml", "workbench-startup.xml",
        "workbench-log-locations.xml"
    };

    [GeneratedRegex(@"^(candidate-[a-fA-F0-9]{32}\.json|external-startup-[0-9]+\.log|crash-[0-9]+-(error\.log|Player\.log|output_log\.txt|crash\.log|crash-[a-zA-Z0-9_-]+\.txt))$", RegexOptions.CultureInvariant)]
    private static partial Regex VariableFiles();

    internal static bool Exportable(string name) => ExportFiles.Contains(name) || VariableFiles().IsMatch(name);

    internal static bool Disposable(string name) => !RecoveryFiles.Contains(name)
        && (Exportable(name) || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
}
