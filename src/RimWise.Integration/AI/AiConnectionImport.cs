using System.Text.Json;

namespace RimWise.Integration.AI;

/// <summary>Offline parsing only. Never executes pasted configuration or returns it in errors.</summary>
public sealed class AiConnectionImport
{
    public string? Endpoint { get; private set; }
    public string? ApiKey { get; private set; }
    public string? Model { get; private set; }
    public override string ToString() => "AI connection information (private)";

    public static AiConnectionImport Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 65536)
            throw new ArgumentException("连接信息为空或过长，请只复制一组连接配置。");
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            if (firstLine < 0 || !text.EndsWith("```", StringComparison.Ordinal)) throw Invalid();
            text = text[(firstLine + 1)..^3].Trim();
        }
        AiConnectionImport result;
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });
                var candidates = new List<AiConnectionImport>();
                ReadJson(document.RootElement, candidates);
                if (candidates.Count != 1) throw Invalid();
                result = candidates[0];
            }
            catch (JsonException) { throw Invalid(); }
        }
        else
        {
            result = new();
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].Trim();
                var separator = line.IndexOfAny(['=', ':', '：']);
                if (separator <= 0) continue;
                result.Set(line[..separator], Unquote(line[(separator + 1)..].Trim()));
            }
        }
        if (result.Endpoint is null && result.ApiKey is null && result.Model is null) throw Invalid();
        if (result.Endpoint is not null)
        {
            // Credentials belong in the secret store, never the ordinary endpoint setting/logs.
            if (!Uri.TryCreate(result.Endpoint, UriKind.Absolute, out var uri)
                || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new ArgumentException("地址无效或含有认证参数，请单独提供 API 地址和密钥。");
            try { result.Endpoint = (AiSettings.Default with { Endpoint = result.Endpoint }).Normalize().Endpoint; }
            catch (ArgumentException) { throw new ArgumentException("API 地址无效。远程地址需使用 HTTPS，本机地址可使用 HTTP。"); }
        }
        if (result.ApiKey is not null && (result.ApiKey.Length > 8192 || result.ApiKey.Any(char.IsWhiteSpace))) throw Invalid();
        if (result.Model is not null && (result.Model.Length > 200 || result.Model.Any(char.IsControl))) throw Invalid();
        return result;
    }

    private static void ReadJson(JsonElement element, List<AiConnectionImport> candidates)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var candidate = new AiConnectionImport();
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String) candidate.Set(property.Name, property.Value.GetString()!);
                else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) ReadJson(property.Value, candidates);
            }
            if (candidate.Endpoint is not null || candidate.ApiKey is not null || candidate.Model is not null) candidates.Add(candidate);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ReadJson(child, candidates);
    }

    private void Set(string name, string value)
    {
        name = name.Trim().Replace("_", "").Replace("-", "").Replace(" ", "").ToLowerInvariant();
        value = value.Trim();
        if (value.Length == 0) return;
        switch (name)
        {
            case "url": case "baseurl": case "apibase": case "apibaseurl": case "apiurl": case "endpoint":
            case "openaiapibase": case "openaibaseurl": case "openaiapiurl":
            case "api地址": case "接口地址": case "服务地址":
                Endpoint = Merge(Endpoint, value); break;
            case "key": case "apikey": case "token": case "accesstoken": case "openaiapikey":
            case "authorization": case "api密钥": case "密钥":
                if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) value = value[7..].Trim();
                ApiKey = Merge(ApiKey, value); break;
            case "model": case "modelname": case "openaimodel": case "模型": case "模型名称":
                Model = Merge(Model, value); break;
        }
    }

    private static string Merge(string? existing, string value) => existing is null || existing == value ? value
        : throw new ArgumentException("发现多组或相互冲突的连接信息，请每次只粘贴一组。");
    private static string Unquote(string value) => value.Length >= 2 && (value[0] == '"' && value[^1] == '"'
        || value[0] == '\'' && value[^1] == '\'') ? value[1..^1] : value;
    private static ArgumentException Invalid() => new("未能识别唯一的连接配置。支持 JSON、环境变量和逐行的地址／密钥／模型；请每次只粘贴一组。");
}
