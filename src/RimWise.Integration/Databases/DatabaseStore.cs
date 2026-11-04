using System.Text.Json;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Databases;

public sealed record DatabaseLoad(DatabaseSnapshot Snapshot, string Status);

/// <summary>Snapshots are immutable. A durable same-volume pointer is published only after both sources validate.
/// Old snapshots remain available for interrupted AI tasks; no automatic pruning of task evidence.</summary>
public sealed class DatabaseStore(string applicationDirectory)
{
    private readonly string root = ScopedPaths.Child(applicationDirectory, "Data/Databases");
    private static readonly Lazy<DatabaseSnapshot> BuiltIn = new(() =>
    {
        using var stream = typeof(DatabaseStore).Assembly.GetManifestResourceStream("RimWise.Databases.snapshot.gz")
            ?? throw new InvalidDataException("缺少内置数据库。");
        return DatabaseParser.Read(stream);
    });
    public DatabaseLoad Load()
    {
        if (ReadSelection() is { } selection)
        {
            foreach (var id in new[] { selection.CurrentId, selection.PreviousId }.OfType<string>())
                try { return new(UpgradeMatchingRequiredItems(LoadPinned(id)), id == selection.CurrentId ? "使用已配置的数据库" : "最新缓存损坏，已回退同来源的上次数据库"); }
                catch (Exception ex) when (Recoverable(ex)) { }
            // A disabled/custom source must never silently fall back to the bundled sources.
            return new(DatabaseParser.Empty(), "配置的数据库缓存不可用，请重新下载或读取；当前未使用数据库");
        }
        var pointer = ScopedPaths.Child(root, "current.json");
        if (!File.Exists(pointer)) return new(BuiltIn.Value, "使用内置离线数据库");
        try
        {
            if (new FileInfo(pointer).Length > 1024) throw new InvalidDataException("数据库索引指针过大。");
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(pointer));
            if (ids is null || ids.Length is < 1 or > 2 || ids.Any(id => !DatabaseParser.Hex(id, 64))) throw new InvalidDataException("数据库索引指针无效。");
            foreach (var id in ids)
                try { return new(UpgradeMatchingRequiredItems(LoadPinned(id)), id == ids[0] ? "使用缓存数据库" : "最新缓存损坏，已回退上次数据库"); }
                catch (Exception ex) when (Recoverable(ex)) { }
        }
        catch (Exception ex) when (Recoverable(ex)) { }
        return new(BuiltIn.Value, "缓存不可用，已回退内置离线数据库");
    }
    public DatabaseSettings LoadSettings() => ReadSelection()?.Settings ?? DatabaseSettings.Default;
    // Only enrich the identical verified workshop source. Custom/disabled sources and task-pinned
    // snapshots retain their meaning; no network, account change or selection write occurs here.
    private static DatabaseSnapshot UpgradeMatchingRequiredItems(DatabaseSnapshot snapshot)
    {
        if (snapshot.Document.RequiredItemsParsed == true || snapshot.IndexedItems == 0
            || snapshot.Document.WorkshopHash != BuiltIn.Value.Document.WorkshopHash
            || snapshot.WorkshopCommit != BuiltIn.Value.WorkshopCommit) return snapshot;
        return DatabaseParser.Create(snapshot.Document with
            { Workshop = BuiltIn.Value.Document.Workshop, RequiredItemsParsed = true });
    }
    private DatabaseSelection? ReadSelection()
    {
        var file = ScopedPaths.Child(root, "selection.json");
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 64 * 1024) throw new InvalidDataException("数据库配置过大。");
        var selection = JsonSerializer.Deserialize<DatabaseSelection>(File.ReadAllBytes(file));
        if (selection is null || selection.Schema != 1 || selection.Settings is null
            || !DatabaseParser.Hex(selection.CurrentId, 64)
            || selection.PreviousId is { } previous && !DatabaseParser.Hex(previous, 64))
            throw new InvalidDataException("数据库配置损坏，请重新配置来源。");
        selection.Settings.Validate();
        return selection;
    }
    public DatabaseSnapshot LoadPinned(string id)
    {
        if (!DatabaseParser.Hex(id, 64)) throw new InvalidDataException("任务数据库快照编号无效。");
        if (BuiltIn.Value.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return BuiltIn.Value;
        var file = ScopedPaths.Child(root, id.ToLowerInvariant() + ".json.gz");
        if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("数据库缓存过大。");
        using var stream = File.OpenRead(file);
        return DatabaseParser.Read(stream, id);
    }

    internal void Install(DatabaseSnapshot snapshot)
    {
        Directory.CreateDirectory(ScopedPaths.Ordinary(root));
        var old = Load().Snapshot;
        // Materialize the built-in snapshot as well, so future executable upgrades can resume its tasks.
        SaveSnapshot(old); SaveSnapshot(snapshot);
        WriteAtomic(ScopedPaths.Child(root, "current.json"), JsonSerializer.SerializeToUtf8Bytes(new[] { snapshot.Id, old.Id }.Distinct().ToArray()));
    }
    public void Preserve(DatabaseSnapshot snapshot)
    {
        Directory.CreateDirectory(ScopedPaths.Ordinary(root)); SaveSnapshot(snapshot);
    }
    private void SaveSnapshot(DatabaseSnapshot snapshot)
    {
        var file = ScopedPaths.Child(root, snapshot.Id + ".json.gz");
        if (File.Exists(file))
        {
            try { if (LoadPinnedFile(file, snapshot.Id)) return; }
            catch (Exception ex) when (Recoverable(ex)) { }
        }
        WriteAtomic(file, DatabaseParser.Compress(snapshot));
    }
    private static bool LoadPinnedFile(string file, string id)
    {
        if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("数据库缓存过大。");
        using var stream = File.OpenRead(file); return DatabaseParser.Read(stream, id).Id == id;
    }
    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = ScopedPaths.Child(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            ScopedPaths.Ordinary(path); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static bool Recoverable(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException
        or JsonException or ArgumentException;

    public Task<DatabaseSnapshot> UpdateAsync(IProgress<string>? progress, CancellationToken token)
        => ApplyAsync(LoadSettings(), true, true, progress, token);

    /// <summary>Download/read changed sources, then atomically publish their settings and validated snapshot together.</summary>
    public async Task<DatabaseSnapshot> ApplyAsync(DatabaseSettings settings, bool refreshCommunity, bool refreshWorkshop,
        IProgress<string>? progress, CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RimWise/0.0.44");
        return await ApplyAsync(client, settings, refreshCommunity, refreshWorkshop, progress, token);
    }
    internal Task<DatabaseSnapshot> UpdateAsync(HttpClient client, IProgress<string>? progress, CancellationToken token)
        => ApplyAsync(client, LoadSettings(), true, true, progress, token);

    internal async Task<DatabaseSnapshot> ApplyAsync(HttpClient client, DatabaseSettings settings, bool refreshCommunity,
        bool refreshWorkshop, IProgress<string>? progress, CancellationToken token)
    {
        settings.Validate(); token.ThrowIfCancellationRequested();
        DatabaseSnapshot old; DatabaseSettings? previousSettings;
        try { previousSettings = LoadSettings(); old = Load().Snapshot; }
        catch (Exception ex) when (Recoverable(ex))
        { old = DatabaseParser.Empty(); previousSettings = null; }
        // If configured snapshots were lost, re-read enabled sources rather than reusing an empty substitute.
        var configured = ReadSelectionIfValid();
        var cacheLost = configured is not null && old.Id != configured.CurrentId && old.Id != configured.PreviousId;
        progress?.Report("正在读取社区规则…");
        var community = refreshCommunity || cacheLost || settings.Community != previousSettings?.Community
            ? await DatabaseSourceReader.ReadAsync(settings.Community, true, client, token) : old;
        progress?.Report("正在读取工坊数据库…");
        var workshop = refreshWorkshop || cacheLost || settings.Workshop != previousSettings?.Workshop
            ? await DatabaseSourceReader.ReadAsync(settings.Workshop, false, client, token) : old;
        token.ThrowIfCancellationRequested();
        var snapshot = DatabaseParser.Combine(community, workshop);
        progress?.Report("正在保存数据库…");
        Directory.CreateDirectory(ScopedPaths.Ordinary(root));
        SaveSnapshot(old); SaveSnapshot(snapshot);
        token.ThrowIfCancellationRequested();
        // Only a snapshot using the very same source configuration may be a fallback.
        var selection = new DatabaseSelection(1, settings, snapshot.Id, settings == previousSettings ? old.Id : null);
        WriteAtomic(ScopedPaths.Child(root, "selection.json"), JsonSerializer.SerializeToUtf8Bytes(selection));
        return snapshot;
    }
    private DatabaseSelection? ReadSelectionIfValid()
    {
        try { return ReadSelection(); }
        catch (Exception ex) when (Recoverable(ex)) { return null; }
    }
}
