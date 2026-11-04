using System.Security.Cryptography;
using System.Text;
using RimWise.Integration.Configuration;

namespace RimWise.Integration.AI;

public sealed record AiTokenEstimate(string Profile, double InputUnits, int Input, int Output, int Lower, int Upper,
    int Samples, int EvaluatedSamples, double? ObservedError80, int? OutputLimit)
{
    public int Total => checked(Input + Output);
    public string Description => $"预计 {Total:N0} token（约 {Lower:N0}—{Upper:N0}）"
        + (Samples < 8 ? "；样本不足，粗略估算" : $"；已参考 {Samples} 次同模型请求")
        + (ObservedError80 is { } error ? $"；近 {EvaluatedSamples} 次中 80% 的预估误差 ≤ {error:P0}" : "；尚无足够记录验证误差");
}

/// <summary>Local, provider/model-specific calibration; stores counts, never prompts or credentials.</summary>
public sealed class AiTokenEstimator(string applicationDirectory)
{
    internal string ApplicationDirectory => applicationDirectory;
    // Usage calibration is replaceable telemetry; do not grow an unbounded backup per request.
    private readonly AppJsonFile<History> history = new(applicationDirectory, "ai-token-history", 1024 * 1024, keepPreviousVersions: false);
    private readonly object sync = new();
    private sealed record Sample(string Profile, double Units, int Predicted, int Input, int Output, DateTimeOffset Utc);
    private sealed record History(int Schema, Sample[] Samples);

    public AiTokenEstimate Estimate(AiRequest request, int? outputLimit = null)
    {
        outputLimit ??= request.OutputTokenLimit;
        if (outputLimit is < 1 or > 32768) throw new ArgumentOutOfRangeException(nameof(outputLimit));
        var profile = Profile(request.Settings, outputLimit);
        var units = Units(AiPrompt.Messages(request));
        Sample[] samples;
        lock (sync) samples = Load().Where(s => s.Profile == profile).TakeLast(40).ToArray();
        var ratio = samples.Length == 0 ? 1 : Quantile(samples.Select(s => s.Input / s.Units), .5);
        var input = Math.Clamp((int)Math.Min(10_000_000, Math.Ceiling(units * ratio)), 1, 10_000_000);
        var output = samples.Length == 0 ? Math.Min(1024, outputLimit ?? 32768)
            : (int)Math.Ceiling(Quantile(samples.Select(s => (double)s.Output), .5));
        if (outputLimit is { } cap) output = Math.Min(output, cap);
        var errors = samples.Where(s => s.Predicted > 0 && s.Input + s.Output > 0)
            .Select(s => Math.Abs(s.Predicted - (s.Input + s.Output)) / (double)(s.Input + s.Output)).ToArray();
        double? error80 = errors.Length >= 8 ? Quantile(errors, .8) : null;
        // The interval expands when recorded errors exceed the target. Never force a fictitious ±20% band.
        var spread = samples.Length < 8 ? .6 : Math.Max(.2, Quantile(errors.DefaultIfEmpty(.6), .95));
        var total = checked(input + output);
        var lower = Math.Max(0, (int)Math.Floor(total * Math.Max(0, 1 - spread)));
        var upper = samples.Length < 8 ? Math.Ceiling(input * 1.6) + (outputLimit ?? 32768)
            : Math.Ceiling(total * (1 + spread));
        return new(profile, units, input, output, lower, (int)Math.Min(int.MaxValue, Math.Max(upper, total)),
            samples.Length, errors.Length, error80, outputLimit);
    }

    public void Observe(AiTokenEstimate estimate, AiTokenUsage usage)
    {
        if (!usage.Valid || usage.Input == 0 || !double.IsFinite(estimate.InputUnits) || estimate.InputUnits < 1
            || estimate.Profile is not { Length: 64 } || estimate.Total is < 1 or > 20_000_000)
            throw new ArgumentException("AI token 记录无效。");
        lock (sync)
        {
            var samples = Load().Append(new Sample(estimate.Profile, estimate.InputUnits, estimate.Total,
                usage.Input, usage.Output, DateTimeOffset.UtcNow)).TakeLast(1000).ToArray();
            history.Save(new(1, samples));
        }
    }

    private Sample[] Load()
    {
        var value = history.Load();
        if (value is null) return [];
        if (value.Schema != 1 || value.Samples is null || value.Samples.Length > 1000
            || value.Samples.Any(s => s is null || s.Profile?.Length != 64 || !double.IsFinite(s.Units) || s.Units < 1
                || s.Input is < 1 or > 10_000_000 || s.Output is < 0 or > 10_000_000
                || (long)s.Input + s.Output > 10_000_000 || s.Predicted is < 1 or > 20_000_000))
            throw new InvalidDataException("AI token 历史记录损坏，请保留文件后检查。");
        return value.Samples;
    }

    private static string Profile(AiSettings settings, int? outputLimit)
    {
        settings = settings.Normalize();
        var endpoint = new Uri(settings.Endpoint);
        // Query strings may contain credentials. Neither they nor API keys enter history.
        var identity = endpoint.GetLeftPart(UriPartial.Path) + "\n" + settings.Model + "\n"
            + (outputLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "uncapped");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static double Units(IEnumerable<AiMessage> messages)
    {
        double value = 3;
        foreach (var message in messages)
        {
            value += 8;
            foreach (var rune in (message.Role + message.Content).EnumerateRunes())
                value += rune.IsAscii ? .28 : rune.Utf8SequenceLength / 2.0;
        }
        return value;
    }

    private static double Quantile(IEnumerable<double> values, double q)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * q) - 1, 0, sorted.Length - 1)];
    }
}
