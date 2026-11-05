namespace RimWise.Integration.AI;

public sealed record AiLogExcerpt(string Source, string Text);
public sealed record AiSymbolOwner(string Symbol, string PackageId, string Basis);
public sealed record AiCodeEvidence(string EvidenceId, string PackageId, string Method, string AssemblyHash,
    string Text, bool Truncated);
public sealed record AiAssemblyFact(string Kind, string PackageId, string RelatedPackageId, string Symbol);
public sealed record AiModFileEvidence(string Kind, string PackageId, int OrderIndex,
    string DefinitionType, string DefinitionName, string Text);

/// <summary>Bounded, locally sanitized diagnostic evidence. Contains no local file paths or executable actions.</summary>
public sealed record AiLogContext(IReadOnlyList<AiLogExcerpt> Excerpts, IReadOnlyList<AiSymbolOwner> Owners,
    bool SelectionLimited)
{
    // Kept separately so a busy startup log cannot hide the exception that stopped the journey.
    public string? BlockingError { get; init; }
    public IReadOnlyList<AiModFileEvidence> Files { get; init; } = [];
    public IReadOnlyList<AiCodeEvidence> Code { get; init; } = [];
    public string CodeStatus { get; init; } = "not-needed";
    public IReadOnlyList<AiAssemblyFact> Assemblies { get; init; } = [];
    internal AiLogContext CopyValidated()
    {
        if (BlockingError is { Length: > 4096 }) throw new InvalidDataException("测试阻塞证据过长。");
        if (Assemblies is null || Assemblies.Count > 16 || Assemblies.Any(f => !ValidAssemblyFact(f)))
            throw new InvalidDataException("程序集诊断上下文无效。");
        if (Code is null || Code.Count > 6 || CodeStatus is not ("not-needed" or "available" or "unavailable" or "timeout" or "limited" or "no-matching-method")
            || Code.Any(c => c is null || !Identifier(c.EvidenceId, 32) || !AiModIdentifier.IsValid(c.PackageId)
                || !Identifier(c.Method, 512) || c.AssemblyHash is null || c.AssemblyHash.Length != 64
                || !c.AssemblyHash.All(char.IsAsciiHexDigit) || string.IsNullOrWhiteSpace(c.Text) || c.Text.Length > 2048)
            || Excerpts is null || Owners is null || Files is null || Files.Count > 24
            || Files.Any(f => f is null || f.Kind is not ("active-definition" or "active-patch" or "inactive-official-definition"
                    or "active-reference" or "active-parent" or "inactive-official-parent")
                || !AiModIdentifier.IsValid(f.PackageId) || !Identifier(f.DefinitionType, 160) || !Identifier(f.DefinitionName, 160)
                || f.OrderIndex < 0 || f.OrderIndex > 20000 || string.IsNullOrEmpty(f.Text) || f.Text.Length > 3072
                || (f.Kind.StartsWith("inactive-official-", StringComparison.Ordinal) ? f.OrderIndex != 0 : f.OrderIndex == 0))
            || Files.Sum(f => f.Text.Length) > 32768 || Excerpts.Count > 48 || Owners.Count > 96
            || Excerpts.Any(e => e is null || e.Source is not ("startup" or "crash" or "game-errors")
                || string.IsNullOrEmpty(e.Text) || e.Text.Length > 4096)
            || Excerpts.Sum(e => e.Text.Length) > 49152
            || Owners.Any(o => o is null || o.Basis is not ("assembly-type" or "xml-symbol")
                || !Identifier(o.Symbol, 256) || !AiModIdentifier.IsValid(o.PackageId)))
            throw new InvalidDataException("诊断日志上下文无效。");
        return this with { Excerpts = Excerpts.ToArray(), Owners = Owners.Distinct().ToArray(), Files = Files.ToArray(), Code = Code.ToArray(), Assemblies = Assemblies.ToArray() };
    }

    internal static bool ValidAssemblyFact(AiAssemblyFact? fact) => fact is not null
        && fact.Kind is "assembly-reference" or "ambiguous-reference" or "shared-assembly" or "different-assembly-images" or "shared-type-name"
        && AiModIdentifier.IsValid(fact.PackageId) && AiModIdentifier.IsValid(fact.RelatedPackageId) && Identifier(fact.Symbol, 512);

    private static bool Identifier(string? value, int maximum) => !string.IsNullOrEmpty(value)
        && value.Length <= maximum && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '+' or '`' or '-');
}
