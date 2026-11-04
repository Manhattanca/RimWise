using RimWise.Integration.AI;

internal static class AiTokenEstimationChecks
{
    internal static void Run(Action<string, Action> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "token estimation 回归", Guid.NewGuid().ToString("N"));
        var estimator = new AiTokenEstimator(root);
        var settings = AiSettings.Default with { Model = "synthetic-model-a", Endpoint = "https://example.invalid/v1/" };
        var request = new AiRequest(settings, null, "1.7", ["ludeon.rimworld"], ["ludeon.rimworld"], [], []);
        var baseline = estimator.Estimate(request, 4096);
        check("Unseen models share an aggregate prior without fictitious observations or accuracy", () =>
        {
            var other = estimator.Estimate(request with { Settings = settings with { Model = "synthetic-model-b" } }, 4096);
            Assert(baseline.Input == other.Input && baseline.Output == other.Output && baseline.Profile != other.Profile);
            Assert(baseline.Input == Math.Ceiling(baseline.InputUnits * .9931544921251937) && baseline.Output == 1363);
            Assert(baseline.Samples == 0 && baseline.EvaluatedSamples == 0 && baseline.ObservedError80 is null
                && baseline.Description.Contains("共享基准") && !baseline.Description.Contains("51"));
            Assert(!Directory.Exists(Path.Combine(root, "Data")) || !Directory.EnumerateFiles(Path.Combine(root, "Data"), "*.json").Any());
        });
        check("Shared output prior respects a smaller output limit and keeps conservative uncertainty", () =>
        {
            var capped = estimator.Estimate(request, 100);
            Assert(capped.Output == 100 && capped.Upper >= Math.Ceiling(capped.Input * 1.6) + 100
                && capped.Lower <= capped.Total && capped.Upper >= capped.Total);
        });
        check("Actual observations replace the prior only for their own profile and survive restart", () =>
        {
            var usage = new AiTokenUsage(2000, 500, 2500);
            estimator.Observe(baseline, usage);
            var own = new AiTokenEstimator(root).Estimate(request, 4096);
            var other = estimator.Estimate(request with { Settings = settings with { Model = "synthetic-model-b" } }, 4096);
            Assert(Math.Abs(own.Input - usage.Input) <= 1 && own.Output == usage.Output && own.Samples == 1);
            Assert(other.Input == baseline.Input && other.Output == baseline.Output && other.Samples == 0);
            Assert(own.ObservedError80 is null && !own.Description.Contains("共享基准"));
        });
        check("Invalid provider usage cannot train the estimator", () =>
        {
            try { estimator.Observe(baseline, new(2000, 500, 0)); }
            catch (ArgumentException)
            {
                Assert(estimator.Estimate(request, 4096).Samples == 1);
                return;
            }
            throw new Exception("Invalid usage accepted");
        });
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new Exception("Token estimation regression");
    }
}
