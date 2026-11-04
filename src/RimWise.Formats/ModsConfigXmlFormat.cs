using System.Text;
using System.Xml;
using System.Xml.Linq;
using RimWise.Core;

namespace RimWise.Formats;

/// <summary>Reads the known ModsConfigData schema; unrelated XML is rejected.</summary>
public sealed class ModsConfigXmlFormat
{
    public const long MaximumCharacters = 16 * 1024 * 1024;
    public const int MaximumElementDepth = 64;

    public ModsConfigDocument Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumCharacters,
            CloseInput = false
        };
        using var reader = XmlReader.Create(input, settings);
        var xml = XDocument.Load(reader);
        if (xml.Descendants().Any(element => element.Ancestors().Take(MaximumElementDepth).Count() >= MaximumElementDepth))
            throw new InvalidDataException("XML 嵌套层级过深，无法导入。");
        var root = xml.Root;
        if (root?.Name != "ModsConfigData")
            throw new InvalidDataException("不是支持的 ModsConfigData 排序文件。其他格式将在后续版本接入。");

        var activeLists = root.Elements("activeMods").ToArray();
        if (activeLists.Length != 1)
            throw new InvalidDataException("排序文件必须包含且只包含一个 activeMods 列表。");
        if (root.Elements("version").Count() > 1)
            throw new InvalidDataException("排序文件包含重复的 version 字段。");

        var entries = activeLists[0].Elements().ToArray();
        if (entries.Any(element => element.Name != "li" || element.HasElements || element.HasAttributes)
            || activeLists[0].Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value)))
            throw new InvalidDataException("activeMods 中存在无法安全转换的结构，原文件未被修改。");

        var order = new LoadOrder(entries.Select(element => element.Value));
        return new ModsConfigDocument(order, xml);
    }

    public byte[] Write(ModsConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            CloseOutput = false
        }))
        {
            document.BuildXml().Save(writer);
        }
        return output.ToArray();
    }
}
