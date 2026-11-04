using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace RimWise.Formats;

public enum OrderFileFormat { ModsConfigXml, RimSortJson, ModListXml, RmlXml, SavegameRws }
public sealed record OrderWorkshopReference(string Identifier, string WorkshopId);

/// <summary>Keeps source-specific fields alongside the common editable configuration.</summary>
public sealed class OrderFileDocument
{
    public ModsConfigDocument Configuration { get; }
    public OrderFileFormat SourceFormat { get; }
    public IReadOnlyDictionary<string, string> Colors { get; }
    public IReadOnlyList<OrderWorkshopReference> WorkshopReferences => OrderListXml.Entries(this)
        .Select(entry => new OrderWorkshopReference(entry.Id, entry.Workshop)).ToArray();
    internal JsonObject? JsonTemplate { get; }
    internal XDocument? ListTemplate { get; }

    internal OrderFileDocument(ModsConfigDocument configuration, OrderFileFormat sourceFormat,
        JsonObject? jsonTemplate = null, XDocument? listTemplate = null, IReadOnlyDictionary<string, string>? colors = null)
    {
        Configuration = configuration;
        SourceFormat = sourceFormat;
        JsonTemplate = (JsonObject?)jsonTemplate?.DeepClone();
        ListTemplate = listTemplate is null ? null : new XDocument(listTemplate);
        Colors = OrderColors.Normalize(colors ?? new Dictionary<string, string>());
    }

    public static OrderFileDocument FromConfiguration(ModsConfigDocument configuration) => new(configuration, OrderFileFormat.ModsConfigXml,
        colors: OrderColors.ReadXml(configuration.BuildXml()));
    public OrderFileDocument WithConfiguration(ModsConfigDocument configuration) => new(configuration, SourceFormat, JsonTemplate, ListTemplate, Colors);
    public OrderFileDocument WithColors(IReadOnlyDictionary<string, string> colors) => new(Configuration, SourceFormat, JsonTemplate, ListTemplate, colors);
}
