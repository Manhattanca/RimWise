using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

internal sealed record GameProbeSignal(string Stage, int Ticks, int Colonists, int Errors)
{
    internal int Warnings { get; init; }
    internal string? Phase { get; init; }
    internal bool MenuPassed { get; init; }
    internal bool FactionsPassed { get; init; }
    internal int PawnsTested { get; init; }
    internal bool MapReady => Stage == "map-ready" && Ticks >= 120 && Colonists > 0
        && MenuPassed && FactionsPassed && PawnsTested > 0;
}

/// <summary>Fresh per-attempt protocol. A log line or an old result can never finish a new test.</summary>
internal static class GameProbe
{
    internal static GameProbeSignal? Progress(string data, string nonce, int processId)
        => ReadSignal(data, nonce, processId, true);
    internal const string PackageId = "rimwise.fixedtesthelper";
    internal static bool StartupReady(string data, string nonce, int processId)
    {
        var path = ScopedPaths.Child(data, "workbench-startup.xml");
        if (!File.Exists(path)) return false;
        using var file = OpenSignal(path);
        if (file.Length is 0 or > 4096) throw new InvalidDataException("启动信号大小无效。");
        using var xml = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 4096 });
        var root = XDocument.Load(xml).Root;
        if (root?.Name != "startup" || root.HasElements || (string?)root.Attribute("nonce") != nonce
            || !int.TryParse((string?)root.Attribute("pid"), out var pid) || pid != processId
            || (string?)root.Attribute("stage") != "startup-ready")
            throw new InvalidDataException("启动信号不属于本轮进程。");
        return true;
    }
    internal static string? ReadNonce(string data)
    {
        var path=ScopedPaths.Child(data,"workbench-probe.xml");
        if (!File.Exists(path)) return null;
        using var file=File.OpenRead(ScopedPaths.Ordinary(path,true));
        if (file.Length is 0 or > 128*1024) throw new InvalidDataException("测试清单大小无效。");
        using var xml=XmlReader.Create(file,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=128*1024 });
        var root=XDocument.Load(xml).Root;
        var nonce=(string?)root?.Attribute("nonce");
        if(root?.Name!="probe" || !Guid.TryParseExact(nonce,"N",out _)) throw new InvalidDataException("测试清单标识无效。");
        return nonce;
    }
    internal static string Prepare(string data, IReadOnlyList<string> order, IReadOnlyList<string>? downloads = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var file = ScopedPaths.Child(data, "workbench-probe.xml");
        if (File.Exists(file) || File.Exists(ScopedPaths.Child(data, "workbench-signal.xml"))
            || File.Exists(ScopedPaths.Child(data, "workbench-startup.xml")))
            throw new IOException("测试信号目录不得复用。");
        new XDocument(new XElement("probe", new XAttribute("nonce", nonce),
            new XAttribute("mode", downloads is null ? "map" : "download"),
            new XElement("order", order.Select(id => new XElement("li", id))),
            new XElement("downloads", (downloads ?? []).Select(id => new XElement("li", id)))))
            .Save(file);
        return nonce;
    }

    internal static GameProbeSignal? Read(string data, string nonce, int processId)
        => ReadSignal(data, nonce, processId, false);

    private static GameProbeSignal? ReadSignal(string data, string nonce, int processId, bool progress)
    {
        var path = ScopedPaths.Child(data, progress ? "workbench-progress.xml" : "workbench-signal.xml");
        if (!File.Exists(path)) return null;
        using var file = OpenSignal(path);
        if (file.Length is 0 or > 4096) throw new InvalidDataException("辅助检测信号大小无效。");
        using var xml = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 4096 });
        var root = XDocument.Load(xml).Root;
        if (root?.Name != (progress ? "progress" : "signal") || root.HasElements || (string?)root.Attribute("nonce") != nonce
            || !int.TryParse((string?)root.Attribute("pid"), out var pid) || pid != processId)
            throw new InvalidDataException("辅助检测信号不属于本轮进程。");
        var stage = (string?)root.Attribute("stage");
        int Number(string name) => int.TryParse((string?)root.Attribute(name), NumberStyles.None,
            CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : throw new InvalidDataException("辅助检测计数无效。");
        if (progress ? !KnownPhase(stage)
            : stage is not ("map-ready" or "map-stalled" or "downloads-ready" or "helper-failed" or "steam-unavailable" or "order-mismatch"
                or "phase-failed" or "world-failed" or "faction-failed" or "map-failed"))
            throw new InvalidDataException("未知辅助检测结果。");
        var phase = (string?)root.Attribute("phase");
        if (phase is not null && !KnownPhase(phase))
            throw new InvalidDataException("未知测试阶段。");
        return new(stage!, Number("ticks"), Number("colonists"), progress ? 0 : Number("errors"))
            { Warnings = root.Attribute("warnings") is null ? 0 : Number("warnings"), Phase = phase,
                MenuPassed = (string?)root.Attribute("menu") == "1", FactionsPassed = (string?)root.Attribute("factions") == "1",
                PawnsTested = root.Attribute("pawns") is null ? 0 : Number("pawns") };
    }

    private static bool KnownPhase(string? phase) => phase is "main-menu" or "scenario-selection" or "storyteller-selection"
        or "world-generation" or "faction-check" or "starting-site" or "ideology-selection" or "starting-pawns"
        or "game-configuration" or "waiting-map" or "pawn-test" or "map-running";

    // Progress is atomically replaced by the game's main thread. Sharing deletion lets
    // the writer replace it while this reader finishes inspecting the previous snapshot.
    private static FileStream OpenSignal(string path) => new(ScopedPaths.Ordinary(path, true),
        FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
}
