using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Steamworks;

namespace RimWise.SteamBridge;

// This process has a fixed protocol, not a command runner. It loads only the bundled SDK.
internal static class Program
{
    private const uint RimWorld = 294100;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private sealed record Request(string Operation, string[] Ids, string? SearchText = null);
    private sealed record Item(string Id, bool Subscribed, bool Installed, string Directory);

    private static int Main()
    {
        Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
        var initialized = false;
        try
        {
            var input = new StringBuilder();
            int ch;
            while ((ch = Console.Read()) >= 0 && ch != '\n')
            {
                if (input.Length >= 65536) throw new InvalidDataException("Steam 请求过大。");
                input.Append((char)ch);
            }
            var request = JsonSerializer.Deserialize<Request>(input.ToString(), Json) ?? throw new InvalidDataException("Steam 请求为空。");
            if (request.Operation is not ("inventory" or "search" or "query" or "subscribe" or "enqueue" or "wait-download" or "unsubscribe")
                || request.Ids is null || request.Ids.Length > 1000
                || request.Ids.Distinct(StringComparer.Ordinal).Count() != request.Ids.Length
                || request.Ids.Any(id => id.Length == 0 || !id.All(char.IsAsciiDigit) || !ulong.TryParse(id, out var n) || n == 0 || n.ToString() != id)
                || (request.Operation is "inventory" or "search" ? request.Ids.Length != 0 : request.Ids.Length == 0)
                || request.Operation == "search" && (string.IsNullOrWhiteSpace(request.SearchText)
                    || request.SearchText.Length > 200 || request.SearchText.Any(char.IsControl)))
                throw new InvalidDataException("Steam 操作或工坊编号无效。");
            Environment.SetEnvironmentVariable("SteamAppId", RimWorld.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", RimWorld.ToString());
            if (!SteamAPI.IsSteamRunning()) throw new IOException("请先启动 Steam 并登录。");
            if (!(initialized = SteamAPI.Init())) throw new IOException("Steam 接口初始化失败；请确认 Steam 正在运行且已登录。");
            if (!SteamUser.BLoggedOn() || SteamUtils.GetAppID().m_AppId != RimWorld
                || !SteamApps.BIsSubscribedApp(new AppId_t(RimWorld)))
                throw new IOException("当前 Steam 账户未登录或没有环世界使用权。");
            if (request.Operation == "search") Search(request.SearchText!.Trim());
            else if (request.Operation == "inventory")
            {
                var count = SteamUGC.GetNumSubscribedItems();
                if (count > 100000) throw new IOException("Steam 订阅清单过大。");
                var ids = new PublishedFileId_t[count];
                var actual = SteamUGC.GetSubscribedItems(ids, count);
                if (actual > count || SteamUGC.GetNumSubscribedItems() != actual) throw new IOException("Steam 订阅清单正在变化，请稍后重试。");
                Write(new { Kind = "result", Success = true, Operation = request.Operation,
                    Items = ids.Take((int)actual).Select(State).ToArray() });
            }
            else
            {
                var results = new List<Item>();
                foreach (var text in request.Ids)
                {
                    var id = new PublishedFileId_t(ulong.Parse(text));
                    VerifyItem(id);
                    if (request.Operation == "subscribe") SubscribeAndDownload(id);
                    else if (request.Operation == "enqueue") Subscribe(id);
                    else if (request.Operation == "wait-download") WaitForDownload(id);
                    else if (request.Operation == "unsubscribe") Unsubscribe(id);
                    results.Add(State(id));
                }
                Write(new { Kind = "result", Success = true, Operation = request.Operation, Items = results });
            }
            return 0;
        }
        catch (Exception ex)
        {
            Write(new { Kind = "result", Success = false, Error = ex.Message });
            return 1;
        }
        finally { if (initialized) SteamAPI.Shutdown(); }
    }

    private static void Search(string text)
    {
        var query = SteamUGC.CreateQueryAllUGCRequest(EUGCQuery.k_EUGCQuery_RankedByTextSearch,
            EUGCMatchingUGCType.k_EUGCMatchingUGCType_Items, new AppId_t(0), new AppId_t(RimWorld), 1);
        if (query == UGCQueryHandle_t.Invalid) throw new IOException("无法创建 Steam 工坊搜索。");
        try
        {
            if (!SteamUGC.SetSearchText(query, text)) throw new IOException("Steam 未接受搜索关键词。");
            var reply = Wait<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(query), TimeSpan.FromSeconds(60));
            if (reply.m_eResult != EResult.k_EResultOK || reply.m_handle != query || reply.m_unNumResultsReturned > 50)
                throw new IOException("Steam 工坊搜索失败：" + reply.m_eResult);
            var items = new List<object>();
            for (uint i = 0; i < reply.m_unNumResultsReturned; i++)
            {
                if (!SteamUGC.GetQueryUGCResult(query, i, out var item) || item.m_eResult != EResult.k_EResultOK
                    || item.m_nConsumerAppID.m_AppId != RimWorld || item.m_nPublishedFileId.m_PublishedFileId == 0) continue;
                items.Add(new { Id = item.m_nPublishedFileId.ToString(), Title = item.m_rgchTitle,
                    ConsumerAppId = item.m_nConsumerAppID.m_AppId });
            }
            Write(new { Kind = "result", Success = true, Operation = "search", Items = items });
        }
        finally { SteamUGC.ReleaseQueryUGCRequest(query); }
    }

    private static void VerifyItem(PublishedFileId_t id)
    {
        var query = SteamUGC.CreateQueryUGCDetailsRequest([id], 1);
        if (query == UGCQueryHandle_t.Invalid) throw new IOException("无法创建工坊资料查询。");
        try
        {
            var reply = Wait<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(query), TimeSpan.FromSeconds(60));
            if (reply.m_eResult != EResult.k_EResultOK || reply.m_handle != query || reply.m_unNumResultsReturned != 1)
                throw new IOException($"Steam 工坊资料查询未完成：{reply.m_eResult}，返回 {reply.m_unNumResultsReturned} 项（编号 {id}）；未变更订阅。");
            if (!SteamUGC.GetQueryUGCResult(query, 0, out var details))
                throw new IOException($"Steam 未提供工坊详情（编号 {id}）；未变更订阅。");
            if (details.m_eResult != EResult.k_EResultOK)
                throw new IOException($"Steam 无法读取工坊项目：{details.m_eResult}（编号 {id}）；可能已隐藏、删除或暂时不可访问，未变更订阅。");
            if (details.m_nPublishedFileId != id || details.m_nConsumerAppID.m_AppId != RimWorld)
                throw new IOException($"Steam 工坊项目不匹配：请求 {id}，返回 {details.m_nPublishedFileId}，所属游戏 {details.m_nConsumerAppID.m_AppId}；未变更订阅。");
        }
        finally { SteamUGC.ReleaseQueryUGCRequest(query); }
    }

    private static void SubscribeAndDownload(PublishedFileId_t id)
    {
        Subscribe(id);
        WaitForDownload(id);
    }

    // Confirm subscription without waiting for content. A collection queues every item before monitoring downloads.
    private static void Subscribe(PublishedFileId_t id)
    {
        Progress(id, "正在订阅");
        var reply = Wait<RemoteStorageSubscribePublishedFileResult_t>(SteamUGC.SubscribeItem(id), TimeSpan.FromSeconds(60));
        if (reply.m_eResult != EResult.k_EResultOK || reply.m_nPublishedFileId != id)
            throw new IOException("Steam 未确认订阅成功：" + reply.m_eResult);
        PumpUntil(() => State(id).Subscribed, TimeSpan.FromSeconds(15));
        Progress(id, "Steam 已确认订阅，等待下载");
    }

    private static void WaitForDownload(PublishedFileId_t id)
    {
        if (!State(id).Subscribed) throw new IOException("Steam 尚未确认订阅，未开始等待下载。");
        DownloadItemResult_t? completed = null;
        using var callback = Callback<DownloadItemResult_t>.Create(result =>
        { if (result.m_nPublishedFileId == id && result.m_unAppID.m_AppId == RimWorld) completed = result; });
        // An already-current installation may not produce a new download callback.
        if (!State(id).Installed && !SteamUGC.DownloadItem(id, true)) throw new IOException("Steam 拒绝下载；订阅已保留。");
        var watch = Stopwatch.StartNew();
        long lastReport = -1;
        while (true)
        {
            SteamAPI.RunCallbacks();
            if (completed is { } result && result.m_eResult != EResult.k_EResultOK)
                throw new IOException("Steam 下载失败：" + result.m_eResult + "；订阅已保留。");
            if (State(id) is { Subscribed: true, Installed: true }) break;
            if (watch.Elapsed > TimeSpan.FromMinutes(15)) throw new TimeoutException("Steam 下载尚未完成；订阅已保留，恢复时将重新查询。");
            if (watch.ElapsedMilliseconds / 1000 != lastReport)
            {
                lastReport = watch.ElapsedMilliseconds / 1000;
                SteamUGC.GetItemDownloadInfo(id, out var bytes, out var total);
                Write(new { Kind = "progress", Id = id.ToString(), Message = "正在下载", Bytes = bytes, Total = total });
            }
            Thread.Sleep(50);
        }
        Progress(id, "下载完成并已核对安装状态");
    }

    private static void Unsubscribe(PublishedFileId_t id)
    {
        Progress(id, "正在取消订阅");
        var reply = Wait<RemoteStorageUnsubscribePublishedFileResult_t>(SteamUGC.UnsubscribeItem(id), TimeSpan.FromSeconds(60));
        if (reply.m_eResult != EResult.k_EResultOK || reply.m_nPublishedFileId != id)
            throw new IOException("Steam 未确认取消订阅：" + reply.m_eResult);
        PumpUntil(() => !State(id).Subscribed, TimeSpan.FromSeconds(15));
    }

    private static Item State(PublishedFileId_t id)
    {
        var state = (EItemState)SteamUGC.GetItemState(id);
        var installed = state.HasFlag(EItemState.k_EItemStateInstalled)
            && !state.HasFlag(EItemState.k_EItemStateNeedsUpdate)
            && !state.HasFlag(EItemState.k_EItemStateDownloading)
            && !state.HasFlag(EItemState.k_EItemStateDownloadPending);
        var directory = "";
        if (installed) installed = SteamUGC.GetItemInstallInfo(id, out _, out directory, 32768, out _)
            && !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory);
        return new(id.ToString(), state.HasFlag(EItemState.k_EItemStateSubscribed), installed, directory);
    }

    private static T Wait<T>(SteamAPICall_t call, TimeSpan timeout) where T : struct
    {
        if (call == SteamAPICall_t.Invalid) throw new IOException("Steam 拒绝接口请求。");
        var done = false; var failed = false; T reply = default;
        using var callback = CallResult<T>.Create((value, failure) => { reply = value; failed = failure; done = true; });
        callback.Set(call);
        PumpUntil(() => done, timeout);
        if (failed) throw new IOException("Steam 网络请求失败；操作结果需重新查询。");
        return reply;
    }

    private static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            SteamAPI.RunCallbacks();
            if (done()) return;
            if (watch.Elapsed >= timeout) throw new TimeoutException("Steam 未及时确认操作结果；请刷新查询，不应当作成功。");
            Thread.Sleep(50);
        }
    }
    private static void Progress(PublishedFileId_t id, string message) => Write(new { Kind = "progress", Id = id.ToString(), Message = message });
    private static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
}
