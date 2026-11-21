#if DEBUG
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Formats;
using RimWise.Integration.Marketplace;

namespace RimWise.Desktop;

internal static class LocalMarketClientSmokeCheck
{
    internal static async Task RunAsync(MainWindow owner, string output, List<string> checks)
    {
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }

        string? copied = null;
        var code = new string('a', 64);
        var recovery = new RecoveryCodeWindow(code, false, value => copied = value) { Owner = owner };
        recovery.Show();
        try
        {
            recovery.CodeBox.SelectAll();
            Check(recovery.CodeBox.IsReadOnly && recovery.CodeBox.SelectedText == code, "恢复码完整可见、只读且可选中复制");
            recovery.CopyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(copied == code && recovery.Status.Text.Contains("已复制"), "恢复码复制按钮只复制完整恢复码");
            PrototypeSmokeCheck.SavePreview(recovery, Path.Combine(output, "recovery-code.png"));
        }
        finally { recovery.Close(); }

        var transport = new Server();
        using var api = new LocalMarketApiClient(new Uri("https://market.example/"), transport);
        var signedIn = await api.LoginAsync("test_author_", "1234567", "测试昵称", false, default);
        Check(signedIn.User.Login == "test_author_", "桌面市场账号允许服务端支持的下划线用户名");
        var replacement = await api.RecoverAsync("test_author_", code, "7654321", default);
        Check(replacement == new string('b', 64) && transport.RecoveryFieldCorrect, "桌面找回密码请求使用服务端 code 字段");
        var mine = await api.GetMineAsync(default);
        Check(mine.Single().PublicationState == "pending", "新投稿尚无公开排序标识时本人列表仍正常显示待审状态");
        var imageReference = new MarketplaceImage("localmarket/" + new string('d', 32),
            MarketplaceJson.ComputeSha256(Server.Image), Server.Image.Length, 0, 0);
        var cover = await api.GetImageAsync(imageReference, default);
        Check(cover.SequenceEqual(Server.Image), "账号市场图片通过自己的 API 下载并校验完整性");
        var manifest = new MarketplaceManifest(1, "rw-" + new string('c', 32), "1.0.0", "测试排序", "test_author_", "", "1.7", [], "999",
            [new("ludeon.rimworld", null), new("unknown.local", null), new("known.workshop", "123")]);
        MarketplaceJson.ValidateManifest(manifest);
        await api.SubmitAsync(manifest, new SilentProgress(), default, false);
        using var serialized = new MemoryStream(Encoding.UTF8.GetBytes(transport.Sort!));
        var parsed = new OrderFileCodec().Read(serialized);
        Check(parsed.Configuration.Order.PackageIds.SequenceEqual(manifest.Mods.Select(item => item.PackageId))
            && parsed.WorkshopReferences.Single(item => item.Identifier == "known.workshop").WorkshopId == "123",
            "桌面投稿保留原顺序和可用工坊编号，不扫描或拒绝未知来源");
        var entry = mine.Single() with { Sha256 = MarketplaceJson.ComputeSha256(Encoding.UTF8.GetBytes(transport.Sort!)) };
        var downloaded = (await api.GetManifestAsync(entry, default)).Value;
        Check(downloaded.Mods.SequenceEqual(manifest.Mods), "下载回读保留缺失模组和工坊订阅提示");
        var updateActions = MarketplacePublishWindow.CreateProductionActions(downloaded,
            Path.Combine(output, "isolated-account-update"), _ => { }, isUpdate: true, accountMarket: api,
            initialImages: new[] { cover });
        Check(updateActions.InitialImages.Single().SequenceEqual(cover), "修改已上架排序时恢复服务器原图片，不会因修改简介丢失封面");
        updateActions.SaveImageDraft!(downloaded, updateActions.InitialImages);
        Check(new MarketplaceDraftStore(Path.Combine(output, "isolated-account-update")).ReadAll().Single()
            .ImageContents.Single().SequenceEqual(cover), "账号市场的更新草稿保存并保留原图片");
        Check(mine.Single().DisplayAuthor == "测试昵称", "市场显示昵称但保留内部用户名作归属校验");
        transport.State = "rejected";
        var returned = (await api.GetMineAsync(default)).Single();
        var returnedManifest = (await api.GetManifestAsync(entry, default)).Value with { RemoteRevisionId = returned.ManifestPath };
        var resubmitActions = MarketplacePublishWindow.CreateProductionActions(returnedManifest,
            Path.Combine(output, "returned-draft"), _ => { }, isUpdate: true, accountMarket: api);
        resubmitActions.SaveDraft!(returnedManifest);
        var savedReturned = new MarketplaceDraftStore(Path.Combine(output, "returned-draft")).ReadAll().Single();
        Check(savedReturned.Manifest.RemoteRevisionId == returned.ManifestPath, "退回排序保存草稿后保留服务器投稿身份");
        await api.SubmitAsync(returnedManifest, new SilentProgress(), default, true);
        Check(transport.Reopened && transport.Replaced && transport.State == "pending" && transport.Created == 1,
            "退回投稿重开原草稿并原位更新重审，不增加版本或新建排序");
        bool locked = false;
        try { await api.SubmitAsync(returnedManifest, new SilentProgress(), default, true); }
        catch (MarketplaceException ex) when (ex.Message.Contains("待审核")) { locked = true; }
        Check(locked, "已重新提交的待审核内容禁止再次修改");
        foreach (var state in new[] { "rejected", "draft", "pending" })
        {
            var orders = new MarketplaceMyOrdersWindow([returned with { PublicationState = state }], []);
            orders.Show();
            orders.OrdersList.SelectedIndex = 0;
            Check(orders.EditButton.IsEnabled == (state != "pending"), "我的排序编辑状态符合服务端状态：" + state);
            orders.Close();
        }
        var staleProfile = Path.Combine(output, "stale-update-version");
        new MarketplaceDraftStore(staleProfile).Save(new string('e',32), downloaded with { Version = "1.0.1" }, true);
        var nextVersionActions = MarketplacePublishWindow.CreateProductionActions(downloaded with { Version = "1.1" },
            staleProfile, _ => { }, isUpdate: true, accountMarket: api);
        Check(nextVersionActions.LoadDraft!() is null, "历史更新草稿不会覆盖当前自动推进的最新版本号");
        transport.State = "draft";
        transport.Images.AddRange(Enumerable.Range(0, 3).Select(i => new Server.StoredImage(
            i.ToString("x32"), "image/png", Server.Image.Length, new string('0', 64))));
        var uploads = Enumerable.Range(0, 3).Select(i => MarketplaceImages.Prepare("author", "rw-" + new string('c', 32), "1.0.0", i, Server.Image)).ToArray();
        await api.SubmitAsync(returnedManifest, uploads, new SilentProgress(), default, true);
        Check(transport.AtomicReplacements == 3 && transport.Images.Count == 3 && transport.State == "pending",
            "满三张的旧草稿修改图片使用原子替换，始终不突破服务端三张限制");
        transport.State = "draft";
        transport.Images[0] = transport.Images[0] with { Sha256 = new string('0', 64) };
        transport.FailUpload = true;
        var preserved = transport.Images.Select(image => image.Id).ToArray();
        bool failedReplacement = false;
        try { await api.SubmitAsync(returnedManifest, uploads, new SilentProgress(), default, true); }
        catch (MarketplaceException) { failedReplacement = true; }
        Check(failedReplacement && transport.Images.Select(image => image.Id).SequenceEqual(preserved) && transport.State == "draft",
            "原子换图失败保留旧图片和可编辑草稿，不提交不完整投稿");
        transport.TamperImage = true;
        bool imageRejected = false;
        try { await api.GetImageAsync(imageReference, default); }
        catch (MarketplaceException ex) when (ex.Message.Contains("完整性")) { imageRejected = true; }
        Check(imageRejected, "账号市场原图片损坏时拒绝静默覆盖更新");
        transport.Tamper = true;
        bool rejected = false;
        try { await api.GetManifestAsync(entry, default); }
        catch (MarketplaceException ex) when (ex.Message.Contains("完整性")) { rejected = true; }
        Check(rejected, "服务器排序下载内容被修改时拒绝导入而不是信任错误数据");
    }

    private sealed class SilentProgress : IProgress<string> { public void Report(string value) { } }
    private sealed class Server : HttpMessageHandler
    {
        private const string Revision = "11111111111141118111111111111111";
        internal string? Sort;
        internal string State = "pending";
        internal bool Reopened, Replaced;
        internal int Created;
        internal bool RecoveryFieldCorrect;
        internal bool Tamper;
        internal bool TamperImage;
        internal sealed record StoredImage(string Id, string Mime, long Bytes, string Sha256);
        internal List<StoredImage> Images = [];
        internal int AtomicReplacements;
        internal bool FailUpload;
        internal static readonly byte[] Image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri!.Host != "market.example") throw new InvalidOperationException("Unexpected origin");
            var path = request.RequestUri.AbsolutePath;
            if (path == "/api/csrf") return Json(new { token = "csrf-fixture" });
            if (path == "/api/images/" + new string('d', 32)) return new(HttpStatusCode.OK)
            { Content = new ByteArrayContent(TamperImage ? new byte[] { 0 } : Image) };
            if (request.Method != HttpMethod.Get && (!request.Headers.Contains("X-RimWise-CSRF") || !request.Headers.Contains("Origin")))
                throw new InvalidOperationException("Missing CSRF or origin");
            if (path == "/api/login") return new(HttpStatusCode.NoContent);
            if (path == "/api/drafts/" + Revision + "/images")
            {
                if (FailUpload) return new(HttpStatusCode.UnsupportedMediaType) { Content = new StringContent("{\"error\":\"图片损坏\"}") };
                var replace = request.RequestUri.Query.Replace("?replace=", "");
                var old = Images.SingleOrDefault(image => image.Id == replace);
                if (old is null && Images.Count >= 3) throw new InvalidOperationException("Client exceeded server capacity");
                if (old is not null) { Images.Remove(old); AtomicReplacements++; }
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                var image = new StoredImage(Guid.NewGuid().ToString("N"), "image/png", bytes.Length, MarketplaceJson.ComputeSha256(bytes));
                Images.Add(image);
                return Json(new { id = image.Id, mime = image.Mime, bytes = image.Bytes, sha256 = image.Sha256 });
            }
            if (path.EndsWith("/reopen", StringComparison.Ordinal)) { Reopened = true; State = "draft"; return new(HttpStatusCode.NoContent); }
            if (path.EndsWith("/submit", StringComparison.Ordinal)) { State = "pending"; return new(HttpStatusCode.NoContent); }
            if (path == "/api/me") return Json(new { username = "test_author_", nickname = "测试昵称" });
            if (path == "/api/recover")
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                RecoveryFieldCorrect = json.RootElement.GetProperty("code").GetString() == new string('a', 64);
                return Json(new { recoveryCode = new string('b', 64) });
            }
            if (path == "/api/mine") return Json(new[] { new
            {
                id = Revision, listingId = (string?)null, version = "1.0.0", author = "测试昵称", ownerUsername = "test_author_",
                sha256 = new string('a', 64), state = State, reason = State == "rejected" ? "请修改简介" : "", metadata = new
                { title = "测试排序", description = "", gameVersions = "1.7", dlc = "", workshopId = "999", releaseNotes = "", modCount = 3 },
                images = Images.Select(image => new { id = image.Id, mime = image.Mime, bytes = image.Bytes, sha256 = image.Sha256 }).ToArray()
            }});
            if (path == "/api/drafts" || path == "/api/drafts/" + Revision)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Sort = json.RootElement.GetProperty("sort").GetString();
                if (request.Method == HttpMethod.Put) Replaced = true; else Created++;
                return Json(new { id = Revision });
            }
            if (path == "/api/versions/" + Revision + "/sort") return new(HttpStatusCode.OK)
            { Content = new StringContent(Tamper ? Sort + " " : Sort!, Encoding.UTF8, "application/xml") };
            throw new InvalidOperationException("Unexpected market API request " + path);
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
#endif
