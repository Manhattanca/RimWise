using RimWise.Integration.AI;

internal static class AiConnectionImportChecks
{
    internal static void Run(Action<string, Action> check)
    {
        check("Connection import supports channel JSON without network", () =>
        {
            var result = AiConnectionImport.Parse("""{"_type":"newapi_channel_conn","key":"sample-secret","url":"https://example.invalid"}""");
            if (result.Endpoint != "https://example.invalid/v1/chat/completions" || result.ApiKey != "sample-secret" || result.Model is not null)
                throw new Exception("Channel import failed");
            if (result.ToString().Contains("sample-secret")) throw new Exception("Secret leaked in display");
        });
        check("Connection import accepts fenced nested aliases and model", () =>
        {
            var result = AiConnectionImport.Parse("```json\n{\"connection\":{\"base_url\":\"http://localhost:1234/v1\",\"api_key\":\"sample-secret\",\"model\":\"sample-model\"}}\n```");
            if (result.Model != "sample-model" || result.Endpoint != "http://localhost:1234/v1/chat/completions") throw new Exception("Nested import failed");
        });
        check("Connection import accepts environment and Chinese labels", () =>
        {
            var environment = AiConnectionImport.Parse("export OPENAI_BASE_URL='https://example.invalid/v1/'\nOPENAI_API_KEY=sample-secret\nOPENAI_MODEL=sample-model");
            var chinese = AiConnectionImport.Parse("API 地址：https://example.invalid/v1/\nAPI 密钥：sample-secret\n模型名称：sample-model");
            if (environment.Endpoint != chinese.Endpoint || environment.ApiKey != chinese.ApiKey || environment.Model != chinese.Model)
                throw new Exception("Aliases differ");
        });
        foreach (var invalid in new[]
        {
            "{\"url\":\"https://example.invalid\",\"base_url\":\"https://other.invalid\"}",
            "[{\"url\":\"https://example.invalid\"},{\"key\":\"sample-secret\"}]",
            "{\"url\":\"https://example.invalid?key=sample-secret\"}",
            "{\"url\":\"https://sample-secret@example.invalid\"}",
            "{\"url\":\"http://example.invalid\"}",
            "{\"key\":\"sample-secret\",broken}",
            "{\"key\":\"sample-secret\\nheader\"}",
            "not a connection"
        })
            check("Invalid or ambiguous connection rejected without exposing text", () =>
            {
                try { AiConnectionImport.Parse(invalid); }
                catch (ArgumentException ex)
                {
                    if (ex.ToString().Contains("sample-secret")) throw new Exception("Secret in failure");
                    return;
                }
                throw new Exception("Invalid connection accepted");
            });
    }
}
