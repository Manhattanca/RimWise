using System.Xml.Linq;
using RimWise.Core;

namespace RimWise.Formats;

/// <summary>Keeps format metadata separate from the editable order.</summary>
public sealed class ModsConfigDocument
{
    private readonly XDocument template;

    public LoadOrder Order { get; }
    public string GameVersion => template.Root?.Element("version")?.Value ?? string.Empty;

    internal ModsConfigDocument(LoadOrder order, XDocument template)
    {
        Order = order;
        this.template = new XDocument(template);
    }

    public ModsConfigDocument WithOrder(LoadOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new ModsConfigDocument(order, template);
    }

    internal XDocument BuildXml()
    {
        var result = new XDocument(template);
        result.Root!.Element("activeMods")!.ReplaceNodes(
            Order.PackageIds.Select(id => new XElement("li", id)));
        return result;
    }

    internal ModsConfigDocument WithGameVersion(GameVersion version)
    {
        var result = new XDocument(template);
        var element = result.Root!.Element("version");
        if (element is null) result.Root.AddFirst(new XElement("version", version.ToString()));
        else element.Value = version.ToString();
        return new ModsConfigDocument(Order, result);
    }
}
