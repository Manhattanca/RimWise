using System.Text.Json;

namespace RimWise.Integration.AI;

public sealed record StructuredAiResponse(
    int SchemaVersion,
    string Summary,
    IReadOnlyList<AiAction> Actions,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<AiConflictSuggestion> Conflicts { get; init; } = [];
    public AiStartupDiagnosis? StartupDiagnosis { get; init; }
    public static StructuredAiResponse Parse(string json)
    {
        if (json.Length > 1024 * 1024) throw new InvalidDataException("AI 响应过大。");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AI 响应必须是 JSON 对象。");
        ValidateFields(root, ["schemaVersion", "summary", "actions", "warnings", "conflicts", "startupDiagnosis"]);
        if (root.TryGetProperty("schemaVersion", out var typedSchema) && (typedSchema.ValueKind != JsonValueKind.Number || !typedSchema.TryGetInt32(out _)))
            throw new InvalidDataException("AI schemaVersion 类型无效。");
        var schema = root.TryGetProperty("schemaVersion", out var schemaProperty) && schemaProperty.TryGetInt32(out var value) ? value : 1;
        if (schema != 1) throw new InvalidDataException("AI 响应版本不受支持。");
        var summary = root.TryGetProperty("summary", out var summaryProperty) && summaryProperty.ValueKind == JsonValueKind.String
            ? summaryProperty.GetString() ?? string.Empty : string.Empty;
        var warnings = ReadStrings(root, "warnings", 100);
        var actions = new List<AiAction>();
        if (root.TryGetProperty("actions", out var actionsProperty))
        {
            if (actionsProperty.ValueKind != JsonValueKind.Array) throw new InvalidDataException("AI actions 必须是数组。");
            foreach (var item in actionsProperty.EnumerateArray().Take(500))
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AI action 必须是对象。");
                ValidateFields(item, ["kind", "packageId", "targetIndex", "requiredDependency", "targetPath", "reason"]);
                actions.Add(new AiAction(
                    ReadString(item, "kind"),
                    ReadNullableString(item, "packageId"),
                    ReadNullableInt(item, "targetIndex"),
                    ReadBool(item, "requiredDependency"),
                    ReadNullableString(item, "targetPath"),
                    ReadNullableString(item, "reason")));
            }
            if (actionsProperty.GetArrayLength() > 500) throw new InvalidDataException("AI actions 数量过多。");
        }
        var conflicts = new List<AiConflictSuggestion>();
        if (root.TryGetProperty("conflicts", out var groups))
        {
            if (groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() > 100)
                throw new InvalidDataException("AI conflicts 必须为最多 100 项的数组。");
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AI conflict 必须是对象。");
                ValidateFields(group, ["packageIds"]);
                if (!group.TryGetProperty("packageIds", out var ids) || ids.ValueKind != JsonValueKind.Array
                    || ids.GetArrayLength() is < 2 or > 20000 || ids.EnumerateArray().Any(id => id.ValueKind != JsonValueKind.String))
                    throw new InvalidDataException("冲突组合必须包含至少两个模组标识。");
                var members = ids.EnumerateArray().Select(id => id.GetString()!).ToArray();
                if (members.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > AiModIdentifier.MaximumLength || id.Any(char.IsControl))
                    || members.Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Length)
                    throw new InvalidDataException("冲突组合包含无效或重复标识。");
                conflicts.Add(new(members));
            }
        }
        AiStartupDiagnosis? diagnosis = null;
        if (root.TryGetProperty("startupDiagnosis", out var startup) && startup.ValueKind != JsonValueKind.Null)
        {
            if (startup.ValueKind != JsonValueKind.Object) throw new InvalidDataException("启动诊断必须是对象。");
            ValidateFields(startup, ["category", "reason", "suggestions"]);
            if (!startup.TryGetProperty("suggestions", out var suggestions) || suggestions.ValueKind != JsonValueKind.Array
                || suggestions.GetArrayLength() > 8 || suggestions.EnumerateArray().Any(s => s.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("启动诊断建议必须为最多 8 项的文字数组。");
            diagnosis = new AiStartupDiagnosis(ReadString(startup, "category"), ReadString(startup, "reason"),
                suggestions.EnumerateArray().Select(s => s.GetString()!).ToArray()).CopyValidated();
        }
        return new StructuredAiResponse(schema, summary, actions, warnings) { Conflicts = conflicts, StartupDiagnosis = diagnosis };
    }

    private static void ValidateFields(JsonElement element, string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("AI 响应包含未知或重复字段：" + property.Name);
    }
    private static string ReadString(JsonElement element, string name) => ReadNullableString(element, name) ?? string.Empty;
    private static string? ReadNullableString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("AI 字段类型无效：" + name);
        return value.GetString();
    }
    private static int? ReadNullableInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result)) throw new InvalidDataException("AI 位置必须为整数。");
        return result;
    }
    private static bool ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("AI 前置标记必须为布尔值。");
        return value.GetBoolean();
    }
    private static IReadOnlyList<string> ReadStrings(JsonElement element, string name, int maximum) =>
        !element.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array
            ? [] : values.EnumerateArray().Take(maximum).Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString() ?? string.Empty).Where(value => value.Length > 0).ToArray();
}
