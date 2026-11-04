using System.Xml.Linq;
using RimWise.Core;

namespace RimWise.Integration.Scanning;

/// <summary>Reads declarations only. It never loads mod assemblies or evaluates mod code.</summary>
public sealed class ModMetadataReader
{
    public ModMetadata Read(string file, string boundary, GameVersion? target)
    {
        var root = ReadOnlyMetadata.ReadXml(file, boundary).Root;
        // Some installed mods use <modMetaData>; accept root casing without
        // loosening the structure or accepting a different XML namespace.
        if (root is null || root.Name.NamespaceName.Length > 0
            || !root.Name.LocalName.Equals("ModMetaData", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不是 ModMetaData 模组信息文件。");
        var hasVersioned = new[] { "modDependencies", "loadBefore", "loadAfter", "incompatibleWith" }
            .Any(name => root.Element(name + "ByVersion") is not null);
        var dependencies = Items(Field(root, "modDependencies", target)).Select(item =>
        {
            var id = Text(item, "packageId", 512);
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("前置声明缺少 packageId。");
            var name = Text(item, "displayName", 1024);
            var url = Text(item, "steamWorkshopUrl", 2048);
            return new ModDependency(id, string.IsNullOrWhiteSpace(name) ? id : name, WorkshopId(url));
        }).ToArray();
        var before = List(Field(root, "loadBefore", target)).Concat(List(Single(root, "forceLoadBefore"))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var after = List(Field(root, "loadAfter", target)).Concat(List(Single(root, "forceLoadAfter"))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new ModMetadata(Text(root, "name", 1024), Text(root, "author", 1024), Text(root, "packageId", 512),
            Array.AsReadOnly(List(Single(root, "supportedVersions"))), Text(root, "targetVersion", 512),
            new ModRequirements(Array.AsReadOnly(dependencies), Array.AsReadOnly(before), Array.AsReadOnly(after),
                Array.AsReadOnly(List(Field(root, "incompatibleWith", target)))), hasVersioned,
            Value(Field(root, "description", target), 256 * 1024))
        { ReleaseVersion = ReleaseVersion(root) };
    }

    // Optional author metadata is only a recommendation, never a reason to hide a valid mod.
    private static string ReleaseVersion(XElement root)
    {
        var fields = root.Elements("modVersion").Take(2).ToArray();
        if (fields.Length != 1 || fields[0].HasElements) return "";
        var value = fields[0].Value.Trim();
        return value.Length <= 128 && !value.Any(char.IsControl) ? value : "";
    }

    internal static string WorkshopId(string value)
    {
        // Extract an ID only; never fetch the metadata URL. Older About.xml files use HTTP
        // or workshop/filedetails links. Subscription still goes through the verified Steam API.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "";
        // Read only the well-known community-file route, never execute a Steam URI from metadata.
        if (uri.Scheme == "steam")
        {
            const string prefix = "steam://url/CommunityFilePage/";
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? NumericWorkshopId(value[prefix.Length..]) : "";
        }
        if (uri.Scheme is not ("https" or "http")
            || !(uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("www.steamcommunity.com", StringComparison.OrdinalIgnoreCase))
            || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Fragment.Length != 0
            || uri.AbsolutePath.TrimEnd('/') is not ("/sharedfiles/filedetails" or "/workshop/filedetails")) return "";
        var ids = uri.Query.TrimStart('?').Split('&').Where(part => part.StartsWith("id=", StringComparison.Ordinal))
            .Select(part => part[3..]).ToArray();
        if (ids.Length != 1) return "";
        return NumericWorkshopId(ids[0]);
    }

    private static string NumericWorkshopId(string id)
    {
        return id.Length > 0 && id.All(char.IsAsciiDigit) && ulong.TryParse(id, out var number) && number > 0
            && number.ToString(System.Globalization.CultureInfo.InvariantCulture) == id ? id : "";
    }

    private static XElement? Field(XElement root, string name, GameVersion? target)
    {
        var versioned = Single(root, name + "ByVersion");
        // An explicitly empty override clears the base list; a missing override falls back.
        var replacement = target is null || versioned is null ? null : Single(versioned, "v" + target.Series);
        return replacement ?? Single(root, name);
    }

    private static XElement? Single(XElement root, string name)
    {
        var values = root.Elements(name).Take(2).ToArray();
        if (values.Length > 1) throw new InvalidDataException($"模组信息包含重复的 {name} 字段。");
        return values.FirstOrDefault();
    }

    private static string Text(XElement root, string name, int maximum) => Value(Single(root, name), maximum);
    private static string Value(XElement? element, int maximum)
    {
        if (element is null) return string.Empty;
        if (element.HasElements || element.Value.Length > maximum) throw new InvalidDataException("模组信息字段结构或长度无效。");
        return element.Value.Trim();
    }

    private static XElement[] Items(XElement? field)
    {
        if (field is null) return [];
        var items = field.Elements().Take(2049).ToArray();
        if (items.Length > 2048 || items.Any(item => item.Name != "li"))
            throw new InvalidDataException("模组信息列表结构或长度无效。");
        return items;
    }
    private static string[] List(XElement? field) => Items(field).Select(item => Value(item, 512)).Where(value => value.Length > 0).ToArray();
}
