using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RimWise.Integration.Diagnostics;

/// <summary>One boundary for persisted telemetry and exported text. Never export credential stores.</summary>
public sealed class DiagnosticRedactor
{
    private static readonly ConcurrentDictionary<string, byte> KnownSecrets = new(StringComparer.Ordinal);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private readonly string[] secrets;
    private static readonly Regex SensitiveField = new(
        "(?i)(api.?key|authorization|credential|password|passwd|secret|access.?token|refresh.?token|cookie|bearer)", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Credentials = new(
        "(?im)^.*(?:authorization\\s*[:=]|(?:api[-_ ]?key|password|passwd|secret|access[-_ ]?token|refresh[-_ ]?token|cookie)\\s*[\\\"']?\\s*[:=]|bearer\\s+).*$",
        RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex KeyShape = new(@"(?i)\b(?:sk-|sess-|ghp_|github_pat_)[a-z0-9_\-]{8,}|\beyJ[a-zA-Z0-9_\-]+\.[a-zA-Z0-9_\-]+\.[a-zA-Z0-9_\-]+", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Url = new(@"https?://[^\s<>""']+", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex UserPath = new(@"(?i)[a-z]:[\\/]Users[\\/][^\\/\r\n""<>]+", RegexOptions.CultureInvariant, Timeout);

    public DiagnosticRedactor(IEnumerable<string>? secretValues = null)
    {
        secrets = (secretValues ?? []).Where(s => !string.IsNullOrEmpty(s))
            .SelectMany(Variants)
            .Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();
    }

    public static void RegisterSecret(string? secret)
    {
        if (!string.IsNullOrEmpty(secret))
            foreach (var value in Variants(secret)) KnownSecrets.TryAdd(value, 0);
    }

    public string Text(string text)
    {
        // Read the live registry: a settings window may register a new key during an export.
        foreach (var secret in secrets.Concat(KnownSecrets.Keys).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length))
            text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        text = Credentials.Replace(text, "[REDACTED credential line]");
        text = KeyShape.Replace(text, "[REDACTED]");
        // Query, fragment and user-info can all carry a provider credential.
        text = Url.Replace(text, match => Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).Replace(uri.UserInfo + "@", "", StringComparison.Ordinal)
                + uri.AbsolutePath + (uri.Query.Length + uri.Fragment.Length > 0 ? "[parameters removed]" : "")
            : "[URL removed]");
        return UserPath.Replace(text, "C:/Users/[USER]");
    }

    public string Json(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new() { Indented = false })) Write(writer, document.RootElement);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static IEnumerable<string> Variants(string value) => [value, Uri.EscapeDataString(value),
        JsonSerializer.Serialize(value)[1..^1], Convert.ToBase64String(Encoding.UTF8.GetBytes(value)),
        System.Net.WebUtility.HtmlEncode(value)];

    private void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(Text(property.Name));
                    if (SensitiveField.IsMatch(property.Name)) writer.WriteStringValue("[REDACTED]");
                    else Write(writer, property.Value);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray()) Write(writer, child);
                writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(Text(element.GetString()!)); break;
            default: element.WriteTo(writer); break;
        }
    }
}
