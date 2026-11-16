using RimWise.Core;
using RimWise.Formats;
using RimWise.Integration.Steam;
using System.Xml.Linq;

namespace RimWise.Integration.Marketplace;

/// <summary>Converts an authored order without inspecting installed mods or changing its sequence.</summary>
public static class MarketplaceOrderCompatibility
{
    /// <summary>Uses the existing RML codec so export and later manual import retain source hints.</summary>
    public static OrderFileDocument ToOrderFile(MarketplaceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        MarketplaceJson.ValidateManifest(manifest);
        if (MarketplaceJson.ParseGameVersions(manifest.GameVersion).Count == 0
            || manifest.Mods.Any(mod => mod.WorkshopId is not null
                && ImportedOrderDownloads.WorkshopIdFromInput(mod.WorkshopId) != mod.WorkshopId)
            || manifest.RequiredDlc.Any(id => !GameContentPolicy.KeepWhenClearing(id, new GameVersion(1, 7))
                || id.Equals("ludeon.rimworld", StringComparison.OrdinalIgnoreCase)
                || !manifest.Mods.Any(mod => mod.PackageId.Equals(id, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("市场排序的游戏版本、必要 DLC 或工坊编号无效。");
        var ids = manifest.Mods.Select(mod => new XElement("li", mod.PackageId)).ToArray();
        var names = manifest.Mods.Select(mod => new XElement("li", mod.PackageId)).ToArray();
        var xml = new XDocument(new XElement("savedModList",
            new XElement("meta", new XElement("gameVersion", manifest.GameVersion),
                new XElement("modIds", ids), new XElement("modSteamIds", manifest.Mods.Select(mod => new XElement("li", mod.WorkshopId ?? "0"))),
                new XElement("modNames", names)),
            new XElement("modList", new XElement("ids", ids.Select(element => new XElement(element))),
                new XElement("names", names.Select(element => new XElement(element))))));
        using var buffer = new MemoryStream();
        xml.Save(buffer); buffer.Position = 0;
        return new OrderFileCodec().Read(buffer);
    }

    /// <summary>Only the collection's declared version is compared; differences never reject import.</summary>
    public static string VersionNotice(MarketplaceManifest manifest, GameVersion runtime) =>
        MarketplaceJson.ParseGameVersions(manifest.GameVersion).Any(value => GameVersion.Parse(value)?.Series == runtime.Series)
            ? "" : $"合集声明游戏版本 {manifest.GameVersion}，当前游戏版本 {runtime.Series}；版本不同仍按原排序导入并保存。";
}
