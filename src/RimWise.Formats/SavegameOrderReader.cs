using System.Xml;
using System.Xml.Linq;

namespace RimWise.Formats;

/// <summary>Reads only the bounded save header; map/pawn data is never loaded or retained.</summary>
internal static class SavegameOrderReader
{
    internal static OrderFileDocument Read(XmlReader reader)
    {
        // Reader is positioned on the root by the content probe. Stop at the first top-level meta.
        reader.ReadStartElement("savegame");
        while (reader.MoveToContent() != XmlNodeType.None)
        {
            if (reader.NodeType == XmlNodeType.EndElement) break;
            if (reader.Depth != 1 || reader.NodeType != XmlNodeType.Element)
                throw new InvalidDataException("存档头结构不正确。");
            if (reader.Name == "meta" && reader.NamespaceURI.Length == 0)
            {
                var meta = (XElement)XNode.ReadFrom(reader);
                if (meta.Descendants().Any(element => element.Ancestors().Take(32).Count() >= 32))
                    throw new InvalidDataException("存档元数据嵌套过深。");
                var ids = OrderFileCodec.XmlList(meta, "modIds", required: true);
                var names = OrderFileCodec.XmlList(meta, "modNames");
                var workshop = OrderFileCodec.XmlList(meta, "modSteamIds");
                if (names.Length > 0 && names.Length != ids.Length || workshop.Length > 0 && workshop.Length != ids.Length)
                    throw new InvalidDataException("存档模组标识、名称或工坊编号数量不一致。");
                var versionNode = OrderFileCodec.Single(meta, "gameVersion");
                if (versionNode?.HasElements == true) throw new InvalidDataException("存档游戏版本字段结构不正确。");
                var version = versionNode?.Value ?? "";
                // Retain only fields needed for a load-order list, never arbitrary save payload.
                var savedMeta = new XElement("meta", new XElement("gameVersion", version),
                    List("modIds", ids), List("modNames", names), List("modSteamIds", workshop));
                var list = new XDocument(new XElement("savedModList", savedMeta,
                    new XElement("modList", List("ids", ids), List("names", names))));
                return new(OrderFileCodec.CreateConfiguration(ids, version, []), OrderFileFormat.SavegameRws, listTemplate: list);
            }
            // Metadata should precede world data; never scan a whole large world looking for it.
            if (reader.Name is "game" or "world" or "maps") break;
            reader.Skip();
        }
        throw new InvalidDataException("存档未找到含 modIds 的头部信息；旧存档若只有模组名称，暂不能可靠还原标识。");
    }
    private static XElement List(string name, IEnumerable<string> values) => new(name, values.Select(value => new XElement("li", value)));
}
