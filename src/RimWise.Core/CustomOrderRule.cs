namespace RimWise.Core;

/// <summary>A directed edge. Inactive endpoints leave the rule dormant.</summary>
public sealed record CustomOrderRule(string Before, string After, bool Enabled = true)
{
    // Session-only invariant. Unlike advisory rules, it cannot be deferred to break a cycle.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Required { get; init; }
    // An insertion baseline is a preference, below actual mod ordering requirements.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? OriginalOrderIndex { get; init; }
    public CustomOrderRule Normalize()
    {
        var before = Before?.Trim() ?? "";
        var after = After?.Trim() ?? "";
        if (before.Length is 0 or > 256 || after.Length is 0 or > 256
            || before.Any(char.IsControl) || after.Any(char.IsControl)
            || before.Equals(after, StringComparison.OrdinalIgnoreCase)
            || OriginalOrderIndex is < 0 or >= 20000 || OriginalOrderIndex is not null && Required)
            throw new ArgumentException("请选择两个不同的模组标识。");
        return this with { Before = before, After = after };
    }
}
