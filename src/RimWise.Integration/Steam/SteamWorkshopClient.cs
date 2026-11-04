using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RimWise.Integration.Safety;
using RimWise.Integration.Diagnostics;

namespace RimWise.Integration.Steam;

public sealed record WorkshopItemState(string Id, bool Subscribed, bool Installed, string Directory);
public sealed record WorkshopSearchItem(string Id, string Title, uint ConsumerAppId);

/// <summary>Async owner of the bundled Steam client bridge. No game or user DLL is loaded.</summary>
public sealed class SteamWorkshopClient(string applicationDirectory)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, MaxDepth = 8 };

    public Task<IReadOnlyList<WorkshopItemState>> InventoryAsync(CancellationToken token) => RunAsync("inventory", [], null, token);
    public Task<IReadOnlyList<WorkshopItemState>> QueryAsync(string id, CancellationToken token) => RunAsync("query", [id], null, token);
    public Task<IReadOnlyList<WorkshopItemState>> SubscribeAndDownloadAsync(string id, IProgress<string>? progress, CancellationToken token)
        => RunAsync("subscribe", [id], progress, token);
    public Task<IReadOnlyList<WorkshopItemState>> EnqueueSubscriptionAsync(string id, IProgress<string>? progress, CancellationToken token)
        => RunAsync("enqueue", [id], progress, token);
    public Task<IReadOnlyList<WorkshopItemState>> WaitForDownloadAsync(string id, IProgress<string>? progress, CancellationToken token)
        => RunAsync("wait-download", [id], progress, token);
    public Task<IReadOnlyList<WorkshopItemState>> UnsubscribeAsync(string id, CancellationToken token)
        => RunAsync("unsubscribe", [id], null, token);

    public async Task<IReadOnlyList<WorkshopSearchItem>> SearchAsync(string text, CancellationToken token)
    {
        text = text.Trim();
        if (text.Length is 0 or > 200 || text.Any(char.IsControl)) throw new InvalidDataException("请输入不超过 200 字的模组名称或标识。");
        var result = await RunProtocolAsync("search", [], null, token, text);
        return ParseSearchResult(result.Line, result.ExitCode);
    }

    private async Task<IReadOnlyList<WorkshopItemState>> RunAsync(string operation, string[] ids, IProgress<string>? progress, CancellationToken token)
    {
        var result = await RunProtocolAsync(operation, ids, progress, token);
        return ParseResult(result.Line, operation, ids, result.ExitCode);
    }

    private async Task<(string Line, int ExitCode)> RunProtocolAsync(string operation, string[] ids, IProgress<string>? progress,
        CancellationToken token, string? searchText = null)
    {
        foreach (var id in ids) ValidateId(id);
        var root = ScopedPaths.Ordinary(Path.Combine(applicationDirectory, "SteamBridge"), true);
        var executable = ScopedPaths.Ordinary(Path.Combine(root, "RimWise.SteamBridge.exe"), true);
        if (!File.Exists(executable)) throw new IOException("Steam 组件缺失，请重新解压完整软件包。");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(operation is "subscribe" or "wait-download" ? TimeSpan.FromMinutes(17) : TimeSpan.FromSeconds(150));
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Steam 组件。");
        try
        {
            // Drain stderr without retaining SDK diagnostics or account data.
            var errors = DrainAsync(process.StandardError, timeout.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Operation = operation, Ids = ids, SearchText = searchText }).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            string? result = null;
            int total = 0;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                total += line.Length;
                if (total > 32 * 1024 * 1024 || line.Length > 16 * 1024 * 1024) throw new IOException("Steam 组件响应过大。");
                // Native SDK startup notices are not protocol messages.
                if (!line.StartsWith('{')) continue;
                using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
                var kind = json.RootElement.GetProperty("Kind").GetString();
                if (kind == "result")
                {
                    if (result is not null) throw new IOException("Steam 组件返回了重复结果。");
                    result = line;
                }
                else if (kind == "progress" && operation is "subscribe" or "enqueue" or "wait-download")
                {
                    var id = json.RootElement.GetProperty("Id").GetString();
                    if (!ids.Contains(id, StringComparer.Ordinal)) throw new IOException("Steam 组件返回了无关项目。");
                    var message = json.RootElement.GetProperty("Message").GetString() ?? "";
                    if (json.RootElement.TryGetProperty("Total", out var all) && all.GetUInt64() > 0)
                        message += $" {json.RootElement.GetProperty("Bytes").GetUInt64() * 100.0 / all.GetUInt64():0}%";
                    progress?.Report($"{id}：{message}");
                }
            }
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            if (result is null) throw new IOException("Steam 组件未返回可核对结果，请确认 Steam 已登录。");
            using var reply = JsonDocument.Parse(result);
            var success = reply.RootElement.GetProperty("Success").GetBoolean();
            DiagnosticEventLog.Current?.Record("steam", operation, $"ids={string.Join(',', ids)}; success={success}; "
                + (success ? "" : reply.RootElement.GetProperty("Error").GetString()));
            return (result, process.ExitCode);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Steam 尚未确认操作完成。已发出的订阅可能仍在下载，请刷新或恢复任务核对。"); }
        finally
        {
            // This is our own bridge, never Steam or an unrelated game process.
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    internal static IReadOnlyList<WorkshopSearchItem> ParseSearchResult(string line, int exitCode)
    {
        using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (!root.GetProperty("Success").GetBoolean())
            throw new IOException(root.TryGetProperty("Error", out var error) ? error.GetString() : "Steam 搜索失败。");
        if (exitCode != 0 || root.GetProperty("Operation").GetString() != "search") throw new IOException("Steam 搜索结果与操作不一致。");
        var items = root.GetProperty("Items").Deserialize<WorkshopSearchItem[]>(Json) ?? throw new IOException("Steam 缺少搜索结果。");
        if (items.Length > 50 || items.Any(i => i is null || i.ConsumerAppId != 294100 || string.IsNullOrWhiteSpace(i.Title) || i.Title.Length > 1024)
            || items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new IOException("Steam 搜索返回了无效项目。");
        foreach (var item in items) ValidateId(item.Id);
        return items;
    }

    internal static IReadOnlyList<WorkshopItemState> ParseResult(string line, string operation, string[] ids, int exitCode)
    {
        using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (!root.GetProperty("Success").GetBoolean())
            throw new IOException(root.TryGetProperty("Error", out var error) ? error.GetString() : "Steam 操作失败。");
        if (exitCode != 0 || root.GetProperty("Operation").GetString() != operation) throw new IOException("Steam 结果与操作不一致。");
        var items = root.GetProperty("Items").Deserialize<WorkshopItemState[]>(Json) ?? throw new IOException("Steam 缺少状态。");
        if (items.Length > 100000 || items.Any(i => i is null) || items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new IOException("Steam 返回了重复或无效项目。");
        foreach (var item in items)
        {
            ValidateId(item.Id);
            if (operation != "inventory" && !ids.Contains(item.Id, StringComparer.Ordinal)) throw new IOException("Steam 返回了无关项目。");
            if (operation == "enqueue" && !item.Subscribed) throw new IOException("Steam 未确认订阅。");
            if (operation is "subscribe" or "wait-download" && (!item.Subscribed || !item.Installed)) throw new IOException("Steam 未完成订阅或下载。");
            if (operation == "unsubscribe" && item.Subscribed) throw new IOException("Steam 尚未取消订阅。");
        }
        if (operation != "inventory" && items.Length != ids.Length) throw new IOException("Steam 结果缺少请求项目。");
        return items;
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiDigit) || !ulong.TryParse(id, out var n) || n == 0 || n.ToString() != id)
            throw new InvalidDataException("无效的工坊编号。");
    }
    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    { var buffer = new char[4096]; while (await reader.ReadAsync(buffer, token) > 0) { } }
}
