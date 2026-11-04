using System.Security.Cryptography;
using System.Text;

namespace RimWise.Integration.AI;

public sealed record AiUsagePeriod(string Scope, AiTaskKind Kind, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc = null);

/// <summary>Minimal billing evidence: no credentials, prompt, path, or raw provider log.</summary>
public sealed record AiUsageRequest(string Id, string Scope, string Model, DateTimeOffset StartedUtc)
{
    public AiTaskKind Kind { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
    public string[] ProviderIds { get; init; } = [];
    public AiTokenUsage? Usage { get; init; }
    public bool ResponseReceived { get; init; }

    public static string ScopeFor(string endpoint, string? key) => Hash(endpoint.TrimEnd('/') + "\n" + key);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string? SafeId(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl)
        ? Hash(value) : null;

    internal void Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || Scope is not { Length: 64 } || !Scope.All(char.IsAsciiHexDigit)
            || !Enum.IsDefined(Kind) || Model is null || Model.Length > 512 || Model.Any(char.IsControl) || StartedUtc == default
            || EndedUtc < StartedUtc || ProviderIds is null || ProviderIds.Length > 8
            || ProviderIds.Any(id => id is not { Length: 64 } || !id.All(char.IsAsciiHexDigit)) || Usage is { Valid: false })
            throw new InvalidDataException("请求用量凭证无效。");
    }
}

public sealed record AiProviderUsageRow(string? LogId, string? RequestId, string Model,
    DateTimeOffset CreatedUtc, long Input, long Output);

public sealed record AiUsageAttribution(long Confirmed, int ConfirmedCount, long Pending, int PendingCount,
    int UnattributedCount, int ConflictingCount)
{
    public static AiUsageAttribution Match(IEnumerable<AiProviderUsageRow> rows, IEnumerable<AiUsageRequest> requests,
        IEnumerable<AiUsagePeriod> periods, string scope)
    {
        var own = requests.Where(r => r.Scope == scope).ToArray();
        var windows = periods.Where(p => p.Scope == scope).ToArray();
        long confirmed = 0, pending = 0;
        int confirmedCount = 0, pendingCount = 0, other = 0, conflicts = 0;
        // Conflicting duplicate server evidence must never inflate or silently alter a confirmed bill.
        var groups = rows.GroupBy(r => r.RequestId ?? r.LogId ?? Guid.NewGuid().ToString("N"));
        foreach (var group in groups)
        {
            var distinct = group.Distinct().ToArray();
            var row = distinct[0];
            if (distinct.Length > 1) { conflicts++; continue; }
            var exact = own.Where(r => row.RequestId is not null && r.ProviderIds.Contains(row.RequestId)
                && r.Model == row.Model && InWindow(row.CreatedUtc, r.StartedUtc, r.EndedUtc)).ToArray();
            if (exact.Length == 1)
            {
                var reported = exact[0].Usage;
                if (reported is not null && (reported.Input != row.Input || reported.Output != row.Output))
                { conflicts++; continue; }
                confirmed = checked(confirmed + row.Input + row.Output); confirmedCount++;
            }
            else if (windows.Any(w => InWindow(row.CreatedUtc, w.StartedUtc, w.EndedUtc)))
            { pending = checked(pending + row.Input + row.Output); pendingCount++; }
            else other++;
        }
        return new(confirmed, confirmedCount, pending, pendingCount, other, conflicts);
    }

    // Provider logs use whole Unix seconds. Allow two seconds of rounding/skew, never an unbounded crash interval.
    private static bool InWindow(DateTimeOffset time, DateTimeOffset start, DateTimeOffset? end) => end is { } stop
        && time >= start.AddSeconds(-2) && time <= stop.AddSeconds(2);
}

public static class AiUsageTracking
{
    public static async Task<AiClientResult> SendAsync(AiTaskRecoveryStore.AiTaskLease task, IAiClient client,
        AiRequest request, CancellationToken token)
        => await SendAsync(task, client, request, token, null).ConfigureAwait(false);

    // The controller commits the safe reply and its accounting in the same immutable checkpoint.
    internal static async Task<AiClientResult> SendAsync(AiTaskRecoveryStore.AiTaskLease task, IAiClient client,
        AiRequest request, CancellationToken token,
        Func<AiTaskCheckpoint, AiClientResult, AiTaskCheckpoint>? commitResult)
    {
        token.ThrowIfCancellationRequested();
        var evidence = new AiUsageRequest(Guid.NewGuid().ToString("N"),
            AiUsageRequest.ScopeFor(request.Settings.Normalize().Endpoint, request.ApiKey), request.Settings.Normalize().Model, DateTimeOffset.UtcNow)
            { Kind = task.Current.Kind };
        task.Save(task.Current with { UsageRequests = [.. task.Current.UsageRequests, evidence] });
        AiClientResult? result = null;
        try
        {
            return result = await client.GetSuggestionAsync(request with { ProviderIdsObserved = ids =>
            {
                evidence = evidence with { ProviderIds = ids.ToArray() };
                task.Save(task.Current with { UsageRequests = task.Current.UsageRequests.Select(r => r.Id == evidence.Id ? evidence : r).ToArray() });
                request.ProviderIdsObserved?.Invoke(ids);
            } }, token).ConfigureAwait(false);
        }
        finally
        {
            var updated = evidence with { EndedUtc = DateTimeOffset.UtcNow,
                ProviderIds = evidence.ProviderIds.Concat(result?.ProviderIds ?? []).Distinct().Take(8).ToArray(),
                ResponseReceived = result is not null, Usage = result?.Usage is { Valid: true } usage ? usage : null };
            var checkpoint = task.Current;
            checkpoint = checkpoint with { UsageRequests = checkpoint.UsageRequests.Select(r => r.Id == evidence.Id ? updated : r).ToArray() };
            if (result is not null && commitResult is not null) checkpoint = commitResult(checkpoint, result);
            task.Save(checkpoint);
        }
    }
}
