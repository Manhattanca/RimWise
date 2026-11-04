using RimWise.Core;
using RimWise.Integration.AI;

namespace RimWise.Integration.Game;

/// <summary>Local log correlation is evidence of an error, never evidence of its cause or of a successful map.</summary>
public static class GameTestDiagnostics
{
    // Helper infrastructure is collected for diagnosis, but is not a mod error and
    // cannot by itself justify excluding an otherwise unrelated installed mod.
    internal static bool IsHelperDiagnostic(GameLogSnapshot log) =>
        Path.GetFileName(log.Path).Equals("workbench-helper-failure.txt", StringComparison.OrdinalIgnoreCase);

    public static AiRuntimeContext? ProjectWarnings(IEnumerable<GameLogSnapshot> logs, bool helperWarning = false)
    {
        var facts = logs.SelectMany(l => l.Warnings).SelectMany(w =>
        {
            var text = w.Detail;
            bool Has(string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
            var code = Has("mipmap") || Has("power of two") || Has("power-of-two") ? "TextureMipmaps"
                : Has("cross-reference") || Has("cross reference") || Has("crossreference") ? "MissingCrossReference"
                : Has("key binding") || Has("keybinding") ? "KeyBindingConflict"
                : Has("DefOf") ? "UninitializedDefOf"
                : Has("translation") ? "TranslationWarning"
                : Has("fractional") || Has("parse") && Has("int") ? "NumericConversion"
                : Has("About.xml") || Has("dependency") && Has("url") ? "ModMetadataWarning" : "UnknownWarning";
            return (w.RelatedMods.Length == 0 ? [""] : w.RelatedMods)
                .Select(id => (Code: code, Id: id, Count: w.Count));
        }).GroupBy(o => (o.Code, o.Id)).Select(g => new AiRuntimeObservation(g.Key.Code, g.Key.Id,
            (int)Math.Min(100000, g.Sum(o => (long)o.Count))))
            .Take(100).ToArray();
        return facts.Length > 0 ? new(facts) : helperWarning ? new([new("UnknownWarning", "", 1)]) : null;
    }

    // These messages identify launch infrastructure, not a mod load-order diagnosis. Keep this
    // deliberately narrow; matching the word "Vulkan" alone would also reject unrelated mod errors.
    public static bool HasLaunchEnvironmentFailure(IEnumerable<GameLogSnapshot> logs) => logs.Any(log =>
        log.Text.Contains("vkCreateInstance: Found no drivers", StringComparison.OrdinalIgnoreCase)
        || log.Text.Contains("vkCreateInstance failed with error: VK_ERROR_INCOMPATIBLE_DRIVER", StringComparison.OrdinalIgnoreCase)
        || log.Text.Contains("windows_read_data_files_in_registry: Registry lookup failed to get ICD manifest files",
            StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<AiDiagnosticFact> Project(IEnumerable<GameLogSnapshot> logs,
        IReadOnlyList<AiSymbolOwner>? owners = null)
    {
        // An owner verified from local XML/PE metadata can associate an error with a candidate
        // installation. It does not prove loading or causation. The trusted controller may use it
        // for a reversible private test; changing the player's order still needs their approval.
        var snapshots = logs.ToArray();
        var verifiedOwners = (owners ?? []).Where(owner => !DiagnosticLogContext.IsFrameworkSymbol(owner.Symbol)).ToArray();
        var blocking = DiagnosticLogContext.BlockingSymbols(snapshots).ToHashSet(StringComparer.Ordinal);
        var blockingFacts = verifiedOwners.Where(owner => owner.Basis == "assembly-type" && blocking.Contains(owner.Symbol))
            .Select(owner => new AiDiagnosticFact("GameTestBlockingFailure", owner.PackageId));
        return blockingFacts.Concat(snapshots.Where(log => !IsHelperDiagnostic(log)).SelectMany(log => log.Issues).SelectMany(issue =>
        {
            var associated = issue.RelatedMods.Concat(verifiedOwners.Where(owner =>
                ContainsSymbol(issue.Detail, owner.Symbol)).Select(owner => owner.PackageId))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var ids = associated.Length == 0 ? [""] : associated;
            var codes = Classify(issue.Detail).Prepend("GameLogError");
            return codes.SelectMany(code => ids.Select(id => new AiDiagnosticFact(code, id)));
        })).Distinct().Take(100).ToArray();
    }

    private static bool ContainsSymbol(string text, string symbol)
    {
        static bool Identifier(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '+' or '`';
        for (var offset = 0; offset < text.Length;)
        {
            var index = text.IndexOf(symbol, offset, StringComparison.Ordinal);
            if (index < 0) return false;
            var end = index + symbol.Length;
            if ((index == 0 || !Identifier(text[index - 1]))
                && (end == text.Length || !Identifier(text[end]) || text[end] == '.')) return true;
            offset = end;
        }
        return false;
    }

    private static IEnumerable<string> Classify(string detail)
    {
        bool Has(string text) => detail.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Has("Could not find parent node") || Has("Could not find parent named") || Has("No parent node found"))
            yield return "GameMissingXmlParent";
        if (Has("Could not find type named") || Has("TypeLoadException") || Has("ReflectionTypeLoadException")
            || Has("MissingMethodException") || Has("Could not load type") || Has("Could not resolve type"))
            yield return "GameTypeLoadFailure";
        if (Has("Patch operation") || Has("PatchOperation") || Has("patch failed")) yield return "GamePatchFailure";
        if (Has("Exception from asynchronous event") || Has("Root_Play.SetupForQuickTestPlay") || Has("Verse.Game..ctor"))
            yield return "GameInitializationFailure";
        if (Has("HarmonyException") || Has("HarmonyLib.PatchClassProcessor")) yield return "GameHarmonyFailure";
    }
}
