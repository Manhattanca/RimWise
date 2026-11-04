using RimWise.Integration.AI;

namespace RimWise.Integration.Configuration;

public sealed record AutomaticSortingPreferences(int SchemaVersion = 1, bool NoticeAcknowledged = false,
    bool AttemptLimitEnabled = false, int MaximumAttempts = 5,
    bool TokenLimitEnabled = false, int TokenBudget = 50_000)
{
    public string Mode { get; init; } = "insert";
    public bool ConfirmModRemoval { get; init; } = true;
}

/// <summary>User preferences survive application upgrades; task checkpoints keep their own limits.</summary>
public sealed class AutomaticSortingPreferencesStore(string applicationDirectory)
{
    private readonly AppJsonFile<AutomaticSortingPreferences> file = new(applicationDirectory, "automatic-sorting");

    public AutomaticSortingPreferences Load()
    {
        var value = file.Load() ?? new();
        if (value.SchemaVersion != 1 || value.MaximumAttempts < 1 || value.TokenBudget < 100
            || value.Mode is not ("insert" or "full" or "adapt" or "offline"))
            throw new InvalidDataException("自动排序设置无效。");
        return value;
    }

    public void AcknowledgeNotice() => file.Save(Load() with { NoticeAcknowledged = true });

    public void SaveMode(string mode, bool confirmRemoval)
    {
        if (mode is not ("insert" or "full" or "adapt" or "offline")) throw new ArgumentException("排序模式无效。");
        file.Save(Load() with { Mode = mode, ConfirmModRemoval = confirmRemoval });
    }

    public void SaveLimits(MaintenanceLimits limits)
    {
        limits.Validate();
        file.Save(Load() with { AttemptLimitEnabled = limits.AttemptLimitEnabled,
            MaximumAttempts = limits.MaximumAttempts, TokenLimitEnabled = limits.TokenLimitEnabled,
            TokenBudget = limits.TokenBudget });
    }
}
