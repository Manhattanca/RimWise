using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimWise.Integration.Configuration;

public sealed record AppearanceControlOverride(string Key, SurfaceOpacitySetting Rule);

/// <summary>Stable UI identities, not labels, row indexes, user content or executable selectors.</summary>
[JsonConverter(typeof(AppearanceControlOverridesConverter))]
public sealed record AppearanceControlOverrides
{
    public AppearanceControlOverride[] Items { get; init; } = [];
    public SurfaceOpacitySetting? Get(string key) => Items.FirstOrDefault(item => item.Key == key)?.Rule;
    public AppearanceControlOverrides With(string key, SurfaceOpacitySetting rule)
    {
        if (!IsKey(key) || rule is null) throw new InvalidDataException("单独透明度控件标识或规则无效。");
        rule.Validate();
        var items = Items.Where(item => item.Key != key).ToList();
        if (rule.Mode != SurfaceOpacityMode.Follow) items.Add(new(key, rule));
        var result = new AppearanceControlOverrides { Items = items.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray() };
        result.Validate(); return result;
    }
    public static bool IsKey(string? key) => key is { Length: >= 3 and <= 120 }
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_');
    internal void Validate()
    {
        if (Items is null || Items.Length > 512) throw new InvalidDataException("单独透明度配置过多或无效。");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            if (item is null || !IsKey(item.Key) || !keys.Add(item.Key) || item.Rule is null)
                throw new InvalidDataException("单独透明度控件标识无效或重复。");
            item.Rule.Validate();
        }
    }
    public bool Equals(AppearanceControlOverrides? other) => ReferenceEquals(this, other)
        || other is not null && Items.OrderBy(item => item.Key, StringComparer.Ordinal)
            .SequenceEqual(other.Items.OrderBy(item => item.Key, StringComparer.Ordinal));
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items.OrderBy(item => item.Key, StringComparer.Ordinal)) hash.Add(item);
        return hash.ToHashCode();
    }
}

/// <summary>A flat map keeps named themes within the existing bounded configuration depth.</summary>
internal sealed class AppearanceControlOverridesConverter : JsonConverter<AppearanceControlOverrides>
{
    public AppearanceControlOverridesConverter() { }
    public override AppearanceControlOverrides Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("单独透明度配置应为对象。");
        var items = new List<AppearanceControlOverride>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                var value = new AppearanceControlOverrides { Items = items.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray() };
                value.Validate(); return value;
            }
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("单独透明度配置无效。");
            var key = reader.GetString();
            if (!AppearanceControlOverrides.IsKey(key) || !keys.Add(key!) || items.Count >= 512)
                throw new InvalidDataException("单独透明度控件标识无效、重复或过多。");
            if (!reader.Read()) throw new JsonException("单独透明度规则缺失。");
            var rule = JsonSerializer.Deserialize<SurfaceOpacitySetting>(ref reader, options)
                ?? throw new InvalidDataException("单独透明度规则缺失。");
            rule.Validate(); items.Add(new(key!, rule));
        }
        throw new JsonException("单独透明度配置未结束。");
    }
    public override void Write(Utf8JsonWriter writer, AppearanceControlOverrides value, JsonSerializerOptions options)
    {
        value.Validate(); writer.WriteStartObject();
        foreach (var item in value.Items.OrderBy(item => item.Key, StringComparer.Ordinal))
        { writer.WritePropertyName(item.Key); JsonSerializer.Serialize(writer, item.Rule, options); }
        writer.WriteEndObject();
    }
}
