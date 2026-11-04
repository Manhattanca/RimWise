using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using RimWise.Core;

namespace RimWise.Formats;

/// <summary>Detects actual content, never a tool name or filename extension.</summary>
public sealed class OrderFileCodec
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    private readonly ModsConfigXmlFormat configurationFormat = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static IReadOnlyList<OrderFileFormat> ExportFormats { get; } = Array.AsReadOnly(new[]
        { OrderFileFormat.ModsConfigXml, OrderFileFormat.RimSortJson, OrderFileFormat.ModListXml, OrderFileFormat.RmlXml });

    public static string DisplayName(OrderFileFormat format) => format switch
    {
        OrderFileFormat.ModsConfigXml => "原版（ModsConfig.xml）",
        OrderFileFormat.RimSortJson => "RimSort JSON",
        OrderFileFormat.ModListXml => "RimCrow ModList XML",
        OrderFileFormat.RmlXml => "RimCrow savedModList RML",
        OrderFileFormat.SavegameRws => "环世界存档中的排序（只读）",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public OrderFileDocument Read(Stream input)
    {
        if (!input.CanSeek)
            return Read(new MemoryStream(ReadBounded(input, MaximumBytes)));
        var start = input.Position;
        using (var typeProbe = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            int initialCharacter;
            var scanned = 0;
            do
            {
                if (++scanned > 65536) throw new InvalidDataException("文件头过长，无法识别排序结构。");
                initialCharacter = typeProbe.Read();
            } while (initialCharacter >= 0 && char.IsWhiteSpace((char)initialCharacter));
            input.Position = start;
            if (initialCharacter == '<')
            {
                using var xmlProbe = XmlReader.Create(input, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = false,
                    MaxCharactersInDocument = ModsConfigXmlFormat.MaximumCharacters
                });
                xmlProbe.MoveToContent();
                if (xmlProbe.Name == "savegame" && xmlProbe.NamespaceURI.Length == 0)
                    return SavegameOrderReader.Read(xmlProbe);
            }
        }
        input.Position = start;
        var bytes = ReadBounded(input, MaximumBytes);
        // Probe only the type. XML bytes are decoded by XmlReader using their own declaration.
        using var probe = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        int first;
        do { first = probe.Read(); } while (first >= 0 && char.IsWhiteSpace((char)first));
        if (first != '{') return ReadXml(bytes);
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return ReadJson(reader.ReadToEnd());
    }

    private OrderFileDocument ReadJson(string text)
    {
        using var parsed = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateKeys(parsed.RootElement);
        var root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("JSON 排序必须为对象。");
        if (!root.ContainsKey("version") || !root.ContainsKey("activeMods"))
            throw new InvalidDataException("不是已支持的 RimSort JSON：需要 version 和 activeMods。");
        var version = root["version"] is null ? "" : JsonString(root["version"]!);
        var ids = JsonList(root["activeMods"], "activeMods");
        var expansions = root.ContainsKey("knownExpansions") ? JsonList(root["knownExpansions"], "knownExpansions") : [];
        var metadata = OrderColors.JsonMetadata(root);
        return new(CreateConfiguration(ids, version, expansions), OrderFileFormat.RimSortJson, root,
            colors: metadata is not null ? OrderColors.ReadJson(metadata) : null);
    }

    private OrderFileDocument ReadXml(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = ModsConfigXmlFormat.MaximumCharacters
        });
        var xml = XDocument.Load(reader);
        if (xml.Descendants().Any(element => element.Ancestors().Take(64).Count() >= 64))
            throw new InvalidDataException("XML 嵌套层级过深。");
        if (xml.Root?.Name == "ModsConfigData")
            return OrderFileDocument.FromConfiguration(configurationFormat.Read(new MemoryStream(bytes)));
        if (xml.Root?.Name == "ModList")
        {
            var ids = XmlList(xml.Root, "modIds", required: true);
            CheckParallel(ids, XmlList(xml.Root, "modNames"));
            CheckParallel(ids, XmlList(xml.Root, "modSteamWorkshopIds"));
            return new(CreateConfiguration(ids, "", []), OrderFileFormat.ModListXml, listTemplate: xml, colors: OrderColors.ReadXml(xml));
        }
        if (xml.Root?.Name == "savedModList")
        {
            var meta = Single(xml.Root, "meta");
            var list = Single(xml.Root, "modList");
            // The editable list owns the order. Metadata can retain an older order;
            // its names and workshop IDs are matched by identity, never by list position.
            var ids = list is not null && Single(list, "ids") is not null
                ? XmlList(list, "ids")
                : meta is not null ? XmlList(meta, "modIds", required: true)
                : throw new InvalidDataException("RML 缺少模组排序列表。");
            var version = meta is null ? "" : Single(meta, "gameVersion")?.Value ?? "";
            return new(CreateConfiguration(ids, version, []), OrderFileFormat.RmlXml, listTemplate: xml, colors: OrderColors.ReadXml(xml));
        }
        throw new InvalidDataException("不支持此文件结构。支持 ModsConfig XML、RimSort JSON、ModList XML、savedModList RML 和含模组标识的环世界存档；不导入应用数据包。");
    }

    public byte[] Write(OrderFileDocument document, OrderFileFormat target)
    {
        if (target == OrderFileFormat.SavegameRws) throw new InvalidDataException("存档只用于提取排序，不生成或修改游戏存档。");
        var config = document.Configuration;
        byte[] result;
        if (target == OrderFileFormat.ModsConfigXml) result = configurationFormat.Write(config);
        else if (target == OrderFileFormat.RimSortJson)
        {
            var json = document.JsonTemplate is { } original ? (JsonObject)original.DeepClone() : new JsonObject();
            json["version"] = config.GameVersion;
            json["activeMods"] = JsonArray(config.Order.PackageIds);
            json["knownExpansions"] = JsonArray(config.BuildXml().Root!.Element("knownExpansions")?.Elements("li").Select(item => item.Value) ?? []);
            var metadata = OrderColors.JsonMetadata(json);
            json.Remove(OrderColors.LegacyJsonProperty);
            if (document.Colors.Count > 0 || metadata is not null)
                json[OrderColors.JsonProperty] = OrderColors.WriteJson(document, metadata);
            result = Encoding.UTF8.GetBytes(json.ToJsonString(JsonOptions));
        }
        else if (target is OrderFileFormat.ModListXml or OrderFileFormat.RmlXml)
            result = OrderListXml.Write(document, target);
        else throw new ArgumentOutOfRangeException(nameof(target));
        if (target != OrderFileFormat.RimSortJson) result = OrderColors.WriteXml(result, document);
        if (result.Length > MaximumBytes) throw new InvalidDataException("导出文件超过大小上限。");
        return result;
    }

    public IReadOnlyList<string> ConversionWarnings(OrderFileDocument document, OrderFileFormat target)
    {
        var warnings = new List<string>();
        if (document.SourceFormat == OrderFileFormat.SavegameRws)
            warnings.Add("仅提取存档的模组顺序与版本信息；不包含地图、人物或其他存档内容，原存档保持不变。");
        if (document.SourceFormat != target)
            warnings.Add("跨格式转换保留原始模组标识和顺序；目标不具备的附加字段不会写入。原文件不变，建议保留排序备份。");
        if (target == OrderFileFormat.ModListXml)
            warnings.Add("ModList 格式不记录游戏版本或 DLC 配置。");
        if (string.IsNullOrWhiteSpace(document.Configuration.GameVersion))
            warnings.Add("来源没有游戏版本信息，本次不猜测或转换版本；导出文件需核对目标游戏版本后使用。");
        if (target is OrderFileFormat.ModsConfigXml or OrderFileFormat.RimSortJson
            && document.SourceFormat is OrderFileFormat.ModListXml or OrderFileFormat.RmlXml or OrderFileFormat.SavegameRws)
            warnings.Add("此目标仅保存启用标识；来源中的模组名称与工坊编号不会写入。");
        if (target is OrderFileFormat.ModListXml or OrderFileFormat.RmlXml && document.ListTemplate is null)
            warnings.Add("来源未提供模组名称与工坊编号；名称使用原始标识，未知工坊编号记为 0，不猜测下载来源。");
        if (target is OrderFileFormat.ModListXml or OrderFileFormat.RmlXml
            && document.Configuration.Order.PackageIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Configuration.Order.PackageIds.Count)
            warnings.Add("RimCrow 导入会合并重复标识；本次导出仍保留原始条目，请留存备份。");
        return warnings;
    }

    internal static byte[] ReadBounded(Stream input, int maximum)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("文件超过读取大小上限。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static ModsConfigDocument CreateConfiguration(IEnumerable<string> ids, string version, IEnumerable<string> expansions) =>
        new(new LoadOrder(ids), new XDocument(new XElement("ModsConfigData", new XElement("version", version),
            new XElement("activeMods"), new XElement("knownExpansions", expansions.Select(value => new XElement("li", value))))));
    private static JsonArray JsonArray(IEnumerable<string> values) => new(values.Select(value => JsonValue.Create(value) as JsonNode).ToArray());
    private static string JsonString(JsonNode value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
        ? text : throw new InvalidDataException("JSON 排序字段类型不正确，需要字符串。");
    private static string[] JsonList(JsonNode? node, string field) => node is JsonArray array
        ? array.Select(value => value is null ? throw new InvalidDataException(field + " 包含空值。") : JsonString(value)).ToArray()
        : throw new InvalidDataException(field + " 必须为字符串数组。");
    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("JSON 包含重复字段：" + property.Name);
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }
    internal static XElement? Single(XElement parent, string name, bool required = false)
    {
        var values = parent.Elements(name).ToArray();
        if (values.Length > 1 || required && values.Length != 1) throw new InvalidDataException("字段缺失或重复：" + name);
        return values.FirstOrDefault();
    }
    internal static string[] XmlList(XElement parent, string name, bool required = false)
    {
        var list = Single(parent, name, required);
        if (list is null) return [];
        if (list.Elements().Any(item => item.Name != "li" || item.HasElements || item.HasAttributes)
            || list.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value)))
            throw new InvalidDataException("列表字段结构不正确：" + name);
        return list.Elements("li").Select(item => item.Value).ToArray();
    }
    private static void CheckParallel(string[] ids, string[] metadata)
    {
        if (metadata.Length != 0 && metadata.Length != ids.Length)
            throw new InvalidDataException("模组标识、名称与工坊编号的数量不一致，无法安全对齐。");
    }
}
