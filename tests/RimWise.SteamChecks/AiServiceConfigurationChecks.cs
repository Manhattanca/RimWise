using System.Net;
using RimWise.Integration.AI;

internal static class AiServiceConfigurationChecks
{
    internal static void Run(Action<string, Action> check)
    {
        check("Model discovery uses GET with key and returns distinct sorted identifiers", () =>
        {
            using var http = new HttpClient(new Handler(request =>
            {
                if (request.Method != HttpMethod.Get || request.RequestUri!.AbsoluteUri != "https://example.invalid/compatible/v1/models"
                    || request.Content is not null || request.Headers.Authorization?.Parameter != "synthetic-secret")
                    throw new Exception("Wrong discovery request");
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"z-model"},{"id":"a-model"},{"id":"z-model"},{"id":""}]}""") };
            }));
            using var client = new AiClient(http);
            var result = client.TestConnectionAsync(AiSettings.Default with { Endpoint = "https://example.invalid/compatible/v1/chat/completions" },
                "synthetic-secret", CancellationToken.None).GetAwaiter().GetResult();
            if (!result.Succeeded || !result.Models.SequenceEqual(new[] { "a-model", "z-model" })) throw new Exception("Wrong models");
        });
        foreach (var status in new[] { 401, 403, 404, 429, 500 })
            check("Discovery classifies HTTP " + status + " without returning server secrets", () =>
            {
                using var http = new HttpClient(new Handler(_ => new((HttpStatusCode)status)
                    { Content = new StringContent("private-response-synthetic-secret") }));
                using var client = new AiClient(http);
                var result = client.TestConnectionAsync(AiSettings.Default, "synthetic-secret", CancellationToken.None).GetAwaiter().GetResult();
                if (result.Succeeded || !result.Message.Contains(status.ToString()) || result.Message.Contains("synthetic-secret")
                    || result.Models.Count != 0) throw new Exception("Invalid error disclosure");
                if (status == 404 && !result.Message.Contains("手动")) throw new Exception("Missing manual fallback");
            });
        check("Service presets normalize, keep compatibility paths, and do not select models", () =>
        {
            foreach (var preset in AiServicePresets.All.Skip(1))
                if (!(AiSettings.Default with { Endpoint = preset.Endpoint }).Normalize().Endpoint.EndsWith("/chat/completions")
                    || AiServicePresets.Find(preset.Endpoint) == 0) throw new Exception("Invalid preset");
            if (AiServicePresets.Find("https://example.invalid/v1") != 0) throw new Exception("Custom host mistaken for preset");
        });
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request));
    }
}
