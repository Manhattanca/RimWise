using System.Text.RegularExpressions;
using RimWise.Integration.AI;

namespace RimWise.Integration.Game;

/// <summary>Fixed observations and bounded technical identifiers supplement the separately sanitized
/// log excerpts. Neither projection grants file, command or removal authority to a model.</summary>
internal static partial class StartupLogDiagnostics
{
    [GeneratedRegex(@"(?:Could not find type named |Could not load type ['"" ]*|TypeLoadException: ['"" ]*)([A-Za-z_][A-Za-z0-9_.+`-]{0,159})(?=['"" ,:\r\n]|$)")]
    private static partial Regex MissingTypes();
    [GeneratedRegex(@"Could not load file or assembly ['""]([A-Za-z_][A-Za-z0-9_.+`-]{0,159})(?=['"",])")]
    private static partial Regex MissingAssemblies();
    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_.+`-]{0,140}Exception)\b")]
    private static partial Regex ExceptionTypes();
    [GeneratedRegex(@"^\s*(?:at\s+)?([A-Za-z_][A-Za-z0-9_.+`-]{0,130}(?::[A-Za-z_][A-Za-z0-9_`]{0,28})?)(?=\s*\()", RegexOptions.Multiline)]
    private static partial Regex StackMethods();
    [GeneratedRegex(@"(?:MissingMethodException: Method not found:|Undefined target method for patch method)\s*['"" ]*(?:[A-Za-z_][A-Za-z0-9_.+`-]*\s+)?([A-Za-z_][A-Za-z0-9_.+`-]{0,159})(?=\s*\()")]
    private static partial Regex TargetMethods();
    [GeneratedRegex(@"(?:\bowner[:=]\s*|\bHarmony ID[:=]\s*)['"" ]*([A-Za-z_][A-Za-z0-9_.+`-]{0,159})(?=['"" ,:\r\n]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PatchOwners();
    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_.+`-]{0,150}\.dll)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NativeModules();
    [GeneratedRegex(@"(?:parent node named [""']|ParentName\s*=\s*[""'])([A-Za-z_][A-Za-z0-9_.+`-]{0,159})")]
    private static partial Regex XmlParents();
    [GeneratedRegex(@"<defName>\s*([A-Za-z_][A-Za-z0-9_.+`-]{0,159})\s*</defName>")]
    private static partial Regex XmlDefinitions();
    [GeneratedRegex(@"<(?:workerClass|thingClass|hediffClass)>\s*([A-Za-z_][A-Za-z0-9_.+`-]{0,159})\s*</")]
    private static partial Regex XmlWorkers();
    [GeneratedRegex(@"^(?:Version:[ \t]*|RimWorld[ \t]+)([0-9]{1,3}\.[0-9]{1,3}(?:\.[0-9]{1,8}){0,2})(?=[ \t\r\n]|$)", RegexOptions.Multiline)]
    private static partial Regex RuntimeVersions();

    internal static StartupFailureDetails Create(OrdinaryGameReport report, string attemptDirectory)
    {
        var evidence = new List<AiStartupEvidence>();
        foreach (var log in report.Logs)
        {
            var source = log.Kind is "game-errors" or "crash" ? log.Kind : "startup";
            var perLog = new List<AiStartupEvidence>();
            // Bound all inspection, including synthetic callers. Log ownership is established by the runner.
            var text = log.Text.Length <= PlayerLogReader.MaximumBytes * 2 ? log.Text : log.Text[^32768..];
            var errorText = string.Join('\n', log.Issues.OrderBy(issue =>
                PlayerLogReader.OccurrenceOffset(log.Text, issue)).Take(8).Select(issue => issue.Detail));
            bool Has(string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
            void Add(bool present, string code) { if (present) perLog.Add(new(code, Source: source)); }
            Add(GameTestDiagnostics.HasLaunchEnvironmentFailure([log]), "GraphicsDriverUnavailable");
            Add(Has("Failed to initialize graphics") || Has("InitializeEngineGraphics failed") || Has("Unable to initialize Vulkan"), "GraphicsInitializationFailed");
            Add(Has("OutOfMemoryException") || Has("Could not allocate memory"), "OutOfMemory");
            Add(!GameTestDiagnostics.IsHelperDiagnostic(log)
                && (Has("UnauthorizedAccessException") || Has("Access is denied")), "AccessDenied");
            Add(Has("DllNotFoundException"), "MissingNativeLibrary");
            Add(Has("MissingMethodException"), "MissingMethod");
            Add(Has("Could not find parent") || Has("No parent node found"), "XmlParentMissing");
            Add(Has("PatchOperation") || Has("Patch operation"), "PatchFailure");
            Add(Has("HarmonyException"), "HarmonyFailure");
            Add(Has("Exception from asynchronous event") || Has("Verse.Game..ctor"), "InitializationFailure");
            Add(Has("Unhandled Exception") || Has("Unhandled exception"), "UnhandledException");
            Add(Has("Access Violation") || Has("EXCEPTION_ACCESS_VIOLATION") || Has("0xc0000005"), "NativeAccessViolation");
            void Symbols(Regex pattern, string code, int count = 6) => perLog.AddRange(pattern.Matches(errorText)
                .Concat(pattern.Matches(text)).Select(m => m.Groups[1].Value.Replace(':', '.')).Distinct().Take(count)
                .Select(symbol => new AiStartupEvidence(code, symbol, source)));
            if (source == "startup") Symbols(RuntimeVersions(), "ObservedGameVersion", 1);
            Symbols(MissingTypes(), "MissingType");
            Symbols(MissingAssemblies(), "MissingManagedAssembly");
            Symbols(ExceptionTypes(), "ExceptionType");
            Symbols(TargetMethods(), "PatchTarget");
            Symbols(PatchOwners(), "PatchOwner");
            Symbols(XmlParents(), "XmlParent");
            Symbols(XmlDefinitions(), "XmlDefinition");
            Symbols(XmlWorkers(), "XmlWorker");
            Symbols(StackMethods(), "StackMethod", 8);
            if (source == "crash") Symbols(NativeModules(), "NativeModule");
            evidence.AddRange(perLog.Distinct());
        }
        if (report.StartFailureCode is { } failure) evidence.Add(new(failure));
        if (report.StopReason == GameTestStopReason.Exited && report.ExitCode != 0) evidence.Add(new("UnexpectedExit"));
        if (report.ProbeStage == "helper-failed") evidence.Add(new("TestHelperFailure"));
        // Historical ErrorDetected reports predate end-of-attempt collection. ExitCode=0
        // and Log closed after a tester stop are not independent crash evidence.
        if (report.StopReason == GameTestStopReason.ErrorDetected) evidence.Add(new("TesterDetectedLogErrors"));
        if (report.StoppedByTester) evidence.Add(new("TesterStoppedProcess"));
        if (report.StopReason == GameTestStopReason.ErrorDetected && !report.StartupObserved && report.ProbeStage is null)
            evidence.Add(new("StoppedBeforeHelperReady"));
        if (report.MenuPassed) evidence.Add(new("MainMenuObserved"));
        if (report.FactionsPassed) evidence.Add(new("FactionsObserved"));
        var phaseCode = report.FailedPhase switch {
            "main-menu" => "MainMenuFailure",
            "scenario-selection" or "storyteller-selection" or "starting-site" or "ideology-selection"
                or "starting-pawns" or "game-configuration" => "NewGameConfigurationFailure", "world-generation" => "WorldGenerationFailure",
            "faction-check" => "FactionGenerationFailure", "waiting-map" => "MapGenerationFailure",
            "pawn-test" or "map-running" => "PawnTestFailure", _ => null };
        if (!report.MapSuccessVerified && phaseCode is not null) evidence.Add(new(phaseCode));
        if (!report.CaptureCompleted || report.StopReason == GameTestStopReason.LogLimit || report.Logs.Any(l => l.Truncated)) evidence.Add(new("LogIncomplete"));
        if (!report.MapSuccessVerified) evidence.Add(new("NoMapSignal"));
        // Reserve source status and spread the limited payload across all three inputs.
        var statuses = new[] { "game-errors", "startup", "crash" }.Select(source => new AiStartupEvidence(
            report.Logs.Any(l => l.Kind == source && l.Text.Length > 0
                || source == "game-errors" && l.Issues.Count > 0) ? "SourceAvailable" : "SourceUnavailable", Source: source));
        var groups = evidence.Distinct().GroupBy(e => e.Source).Select(g => g.Take(28)).ToArray();
        var context = new AiStartupContext(report.StopReason.ToString(), report.Logs.Any(l => l.Text.Length > 0),
            report.CaptureCompleted && report.StopReason != GameTestStopReason.LogLimit && !report.Logs.Any(l => l.Truncated), statuses
                .Concat(groups.OrderByDescending(g => g.Any(e => e.Source == "general")).SelectMany(g => g)).Take(96).ToArray())
            { BoundedErrorCaptureVerified = report.BoundedErrorCaptureVerified }.CopyValidated();
        // These paths come only from the trusted attempt directory, never from AI prose.
        var paths = report.Logs.Select(l => l.Path).Concat(new[] { "launch.json", "launch-failure.log", "result.json", "diagnostic-sources.json", "diagnostic-context.json" }
            .Select(name => Path.Combine(attemptDirectory, name)).Select(Diagnostics.StoredEvidence.Resolve).Where(File.Exists))
            .Select(Diagnostics.StoredEvidence.Resolve).Distinct().ToArray();
        var observations = report.Logs.SelectMany(log => log.Issues.OrderBy(issue =>
                PlayerLogReader.OccurrenceOffset(log.Text, issue)))
            .Select(issue => DiagnosticLogContext.Sanitize(issue.Summary)
                + (issue.RelatedMods.Length > 0 ? " [" + string.Join(", ", issue.RelatedMods.Take(4)) + "]" : ""))
            .Distinct().Take(4).Select(text => text.Length <= 800 ? text : text[..800] + "…").ToArray();
        return new(context, paths) { Observations = observations };
    }
}
