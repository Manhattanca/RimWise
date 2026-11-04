using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RimWise.Formats;

internal static class OrderListXml
{
    internal static byte[] Write(OrderFileDocument document, OrderFileFormat target)
    {
        var xml = document.SourceFormat == target && document.ListTemplate is { } template
            ? new XDocument(template) : new XDocument(new XElement(target == OrderFileFormat.ModListXml ? "ModList" : "savedModList"));
        var root = xml.Root!;
        var entries = Entries(document);
        if (target == OrderFileFormat.ModListXml)
        {
            if (root.Element("Name") is null) root.AddFirst(new XElement("Name", "ModList"));
            SetList(root, "modIds", entries.Select(entry => entry.Id));
            SetList(root, "modNames", entries.Select(entry => entry.Name));
            SetList(root, "modSteamWorkshopIds", entries.Select(entry => entry.Workshop));
        }
        else
        {
            var meta = GetOrAdd(root, "meta");
            GetOrAdd(meta, "gameVersion").Value = document.Configuration.GameVersion;
            SetList(meta, "modIds", entries.Select(entry => entry.Id));
            SetList(meta, "modNames", entries.Select(entry => entry.MetaName));
            SetList(meta, "modSteamIds", entries.Select(entry => entry.Workshop));
            var list = GetOrAdd(root, "modList");
            SetList(list, "ids", entries.Select(entry => entry.Id));
            SetList(list, "names", entries.Select(entry => entry.Name));
        }
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true })) xml.Save(writer);
        return output.ToArray();
    }

    internal static (string Id, string Name, string MetaName, string Workshop)[] Entries(OrderFileDocument document)
    {
        // Match metadata by original identifier and occurrence, keeping duplicates and unresolved entries.
        var original = new Dictionary<string, Queue<(string Name, string MetaName, string Workshop)>>(StringComparer.OrdinalIgnoreCase);
        var displayNames = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
        if (document.ListTemplate?.Root is { } root)
        {
            var isRml = document.SourceFormat is OrderFileFormat.RmlXml or OrderFileFormat.SavegameRws;
            var parent = isRml ? root.Element("meta") ?? new XElement("meta") : root;
            var ids = OrderFileCodec.XmlList(parent, "modIds");
            var names = isRml ? Array.Empty<string>() : OrderFileCodec.XmlList(root, "modNames");
            var metaNames = OrderFileCodec.XmlList(parent, "modNames");
            if (isRml && root.Element("modList") is { } list)
            {
                var listIds = OrderFileCodec.XmlList(list, "ids");
                var listNames = OrderFileCodec.XmlList(list, "names");
                for (var index = 0; index < Math.Min(listIds.Length, listNames.Length); index++)
                {
                    if (!displayNames.TryGetValue(listIds[index].Trim(), out var queue))
                        displayNames[listIds[index].Trim()] = queue = new();
                    queue.Enqueue(listNames[index]);
                }
            }
            if (isRml) names = metaNames;
            var workshop = OrderFileCodec.XmlList(parent, isRml ? "modSteamIds" : "modSteamWorkshopIds");
            for (var index = 0; index < ids.Length; index++)
            {
                if (!original.TryGetValue(ids[index].Trim(), out var queue)) original[ids[index].Trim()] = queue = new();
                queue.Enqueue((index < names.Length ? names[index] : ids[index], index < metaNames.Length ? metaNames[index] : ids[index],
                    index < workshop.Length ? workshop[index] : "0"));
            }
        }
        return document.Configuration.Order.PackageIds.Select(id =>
        {
            var info = original.TryGetValue(id, out var queue) && queue.Count > 0 ? queue.Dequeue() : (Name: id, MetaName: id, Workshop: "0");
            if (displayNames.TryGetValue(id, out var nameQueue) && nameQueue.Count > 0)
                info.Name = nameQueue.Dequeue();
            return (id, info.Name.Length > 0 ? info.Name : id, info.MetaName.Length > 0 ? info.MetaName : id, info.Workshop.Length > 0 ? info.Workshop : "0");
        }).ToArray();
    }
    private static XElement GetOrAdd(XElement parent, string name)
    {
        if (parent.Element(name) is { } element) return element;
        var created = new XElement(name);
        parent.Add(created);
        return created;
    }
    private static void SetList(XElement parent, string name, IEnumerable<string> values) =>
        GetOrAdd(parent, name).ReplaceNodes(values.Select(value => new XElement("li", value)));
}
