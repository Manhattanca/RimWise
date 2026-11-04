using RimWise.Core;

namespace RimWise.Integration.Scanning;

/// <summary>Inspects PE headers without loading assemblies or following directory links.</summary>
public static class ModCodeKindReader
{
    public static ModCodeKind Read(string root, CancellationToken cancellationToken = default) =>
        ModContentReader.Read(root, cancellationToken).CodeKind;
}
