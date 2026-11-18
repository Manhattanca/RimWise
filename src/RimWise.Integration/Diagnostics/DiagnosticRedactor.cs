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
    // Logs and JSON string values can contain megabytes without a newline. Backtracking
    // from ^.* at every candidate position made benign long lines time out during export.
    private const RegexOptions Linear = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private readonly string[] secrets;
    private bool export;
    private CancellationToken cancellationToken;
    private static readonly Regex SensitiveField = new(
        "(?i)(api.?key|authorization|credential|password|passwd|secret|access.?token|refresh.?token|cookie|bearer)", Linear, Timeout);
    private static readonly Regex Credentials = new(
        "(?im)^.*(?:authorization\\s*[:=]|(?:api[-_ ]?key|password|passwd|secret|access[-_ ]?token|refresh[-_ ]?token|cookie)\\s*[\\\"']?\\s*[:=]|bearer\\s+).*$",
        Linear, Timeout);
    private static readonly Regex KeyShape = new(@"(?i)\b(?:sk-|sess-|ghp_|github_pat_)[a-z0-9_\-]{8,}|\beyJ[a-zA-Z0-9_\-]+\.[a-zA-Z0-9_\-]+\.[a-zA-Z0-9_\-]+", Linear, Timeout);
    private static readonly Regex Url = new(@"https?://[^\s<>""']+", Linear, Timeout);
    private static readonly Regex UserPath = new(@"(?i)[a-z]:[\\/]Users[\\/][^\\/\r\n""<>]+", Linear, Timeout);
    private static readonly Regex[] ExportPatterns = new[] { SensitiveField, Credentials, KeyShape, Url, UserPath }
        .Select(pattern => new Regex(pattern.ToString(), Linear, Regex.InfiniteMatchTimeout)).ToArray();

    // Export is an explicit, cancellable background operation. Linear matching does not
    // need a wall-clock cutoff that would discard an otherwise valid large record.
    internal static DiagnosticRedactor ForExport(IEnumerable<string> values, CancellationToken token) =>
        new(values) { export = true, cancellationToken = token };

    internal string[] SecretValues() => secrets.Concat(KnownSecrets.Keys).Distinct(StringComparer.Ordinal)
        .OrderByDescending(s => s.Length).ToArray();

    private Regex Pattern(Regex ordinary, int index) => export ? ExportPatterns[index] : ordinary;

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
        foreach (var secret in SecretValues())
        {
            cancellationToken.ThrowIfCancellationRequested();
            text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }
        cancellationToken.ThrowIfCancellationRequested();
        text = Pattern(Credentials, 1).Replace(text, "[REDACTED credential line]");
        cancellationToken.ThrowIfCancellationRequested();
        text = Pattern(KeyShape, 2).Replace(text, "[REDACTED]");
        // Query, fragment and user-info can all carry a provider credential.
        cancellationToken.ThrowIfCancellationRequested();
        text = Pattern(Url, 3).Replace(text, match => Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).Replace(uri.UserInfo + "@", "", StringComparison.Ordinal)
                + uri.AbsolutePath + (uri.Query.Length + uri.Fragment.Length > 0 ? "[parameters removed]" : "")
            : "[URL removed]");
        cancellationToken.ThrowIfCancellationRequested();
        return Pattern(UserPath, 4).Replace(text, "C:/Users/[USER]");
    }

    internal void Json(Stream input, Stream output)
    {
        // Retain the original reader's UTF-8/BOM/UTF-16 compatibility. JSON is one
        // structured record; unlike line logs its DOM still scales with that document.
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), true, 65536, leaveOpen: true);
        using var document = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions { MaxDepth = 128 });
        using var writer = new Utf8JsonWriter(output);
        Write(writer, document.RootElement);
    }

    internal string RecoverMalformedJson(string raw)
    {
        // Decode escaped property names before checking credential markers. A broken
        // record must never use a weaker privacy boundary than a valid JSON object.
        var decoded = Regex.Replace(raw, @"\\u([a-fA-F0-9]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString(),
            Linear, Regex.InfiniteMatchTimeout);
        return Pattern(SensitiveField, 0).IsMatch(decoded) ? "[REDACTED malformed credential record]" : Text(decoded);
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
        cancellationToken.ThrowIfCancellationRequested();
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(Text(property.Name));
                    if (Pattern(SensitiveField, 0).IsMatch(property.Name)) writer.WriteStringValue("[REDACTED]");
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
