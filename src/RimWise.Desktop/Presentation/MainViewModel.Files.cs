using RimWise.Formats;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private OrderFileDocument? sourceDocument;

    public void LoadFile(OrderFileDocument loaded, string label)
    {
        ImportColors(loaded);
        Load(loaded.Configuration, label);
        sourceDocument = loaded;
    }

    public OrderFileDocument CreateOrderFile()
    {
        var ids = Rows.Select(r => r.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (sourceDocument
        ?? OrderFileDocument.FromConfiguration(CreateExportDocument())).WithConfiguration(CreateExportDocument())
        .WithColors(markerColors.Where(p => ids.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
    }
}
