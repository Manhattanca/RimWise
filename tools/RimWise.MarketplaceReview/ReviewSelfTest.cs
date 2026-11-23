using System.Net;
using System.Text;
using System.Text.Json;

namespace RimWise.MarketplaceReview;

internal static class ReviewSelfTest
{
    internal static async Task<int> Run(string directory)
    {
        Directory.CreateDirectory(directory);
        File.Delete(Path.Combine(directory, "failure.txt"));
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks.Add(name);
        }

        try
        {
            using (var window = new ReviewWindow(false, selfTest: true))
            {
                window.Show();
                window.PerformLayout();
                using var bitmap = new System.Drawing.Bitmap(window.Width, window.Height);
                window.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, window.Width, window.Height));
                bitmap.Save(Path.Combine(directory, "review-window.png"));
                Check(window.Text.Contains("审核助手"), "Chinese review UI renders without login or network");
                var item = new MarketReviewItem(new string('1', 32), null, "1.0", "测试排序名称", "测试昵称", "hidden_username", "pending",
                    new string('a', 64), "", "1.7", string.Concat(Enumerable.Repeat("这是一段用于审核的长简介。", 100)), "", null, "", 2, []);
                var pictures = new List<ReviewImage>();
                for (var index = 0; index < 3; index++)
                {
                    using var picture = new System.Drawing.Bitmap(700, 400);
                    using var graphics = System.Drawing.Graphics.FromImage(picture);
                    graphics.Clear(System.Drawing.Color.FromArgb(35 + index * 30, 105, 110));
                    graphics.DrawString("排序展示图片 " + (index + 1), window.Font, System.Drawing.Brushes.White, 35, 35);
                    using var data = new MemoryStream(); picture.Save(data, System.Drawing.Imaging.ImageFormat.Png);
                    pictures.Add(new(data.ToArray()));
                }
                for (var i = 0; i < 8; i++) window.PreviewForChecks(new(item, "完整辅助信息", pictures, true, ""));
                Check(window.ContentVisibleForChecks, "Review name description and image area are independently visible");
                window.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, window.Width, window.Height));
                bitmap.Save(Path.Combine(directory, "review-content-three-images.png"));
                window.PreviewForChecks(new(item with { Description = "" }, "辅助信息", [], true, ""));
                Check(window.ContentVisibleForChecks, "Optional empty description and images have explicit placeholders");
                foreach (bool approve in new[] { true, false })
                {
                    var afterDecision = new Server(failAfterDecision: true);
                    using var decisionApi = new ReviewApi(afterDecision, new Uri("https://market.example/"));
                    var viewed = await decisionApi.InspectAsync((await decisionApi.ListAsync(default)).Single(), default);
                    using var decisionWindow = new ReviewWindow(false, selfTest: true, testApi: decisionApi);
                    decisionWindow.Show(); decisionWindow.PreviewForChecks(viewed);
                    await decisionWindow.DecideForChecksAsync(viewed, approve);
                    Check(afterDecision.Decisions == 1 && decisionWindow.StatusForChecks.Contains("服务器已确认")
                        && decisionWindow.StatusForChecks.Contains("无需重复提交"),
                        approve ? "Committed approval remains successful when refresh fails" : "Committed rejection remains successful when refresh fails");
                    decisionWindow.Hide();
                }

                window.Hide();
            }

            foreach (var mode in new[] { "anonymous", "list401", "list403", "role", "offline", "image401", "mutation401", "csrf403", "committed401" })
            {
                var authServer = new Server(withImage: mode == "image401");
                using var authApi = new ReviewApi(authServer, new Uri("https://market.example/"));
                using var authWindow = new ReviewWindow(false, selfTest: true, testApi: authApi);
                authWindow.Show();
                await authWindow.LoginForChecksAsync();
                Check(authWindow.LoggedInForChecks && !authWindow.LoginEnabledForChecks && authWindow.ItemsForChecks == 1,
                    mode + ": initial verified login and pending list agree");
                var viewed = await authApi.InspectAsync((await authApi.ListAsync(default)).Single(), default);
                authWindow.PreviewForChecks(viewed);
                authServer.AnonymousMe = mode == "anonymous";
                authServer.ReviewStatus = mode == "list401" ? HttpStatusCode.Unauthorized : mode == "list403" ? HttpStatusCode.Forbidden : null;
                authServer.RoleRemoved = mode == "role";
                authServer.Offline = mode == "offline";
                authServer.ImageUnauthorized = mode == "image401";
                authServer.MutationStatus = mode == "mutation401" ? HttpStatusCode.Unauthorized : mode == "csrf403" ? HttpStatusCode.Forbidden : null;
                authServer.ExpireAfterDecision = mode == "committed401";
                if (mode is "mutation401" or "csrf403" or "committed401") await authWindow.DecideForChecksAsync(viewed, true);
                else if (mode == "image401") await authWindow.InspectForChecksAsync();
                else await authWindow.CheckForChecksAsync();
                if (mode == "committed401")
                    Check(authWindow.StatusForChecks.Contains("服务器已确认") && authWindow.StatusForChecks.Contains("无需重复提交"),
                        "Committed approval remains confirmed when authentication expires during its refresh");
                if (mode is "offline" or "csrf403")
                {
                    Check(authWindow.LoggedInForChecks && !authWindow.LoginEnabledForChecks && authWindow.ItemsForChecks == 1,
                        mode + ": transient network or CSRF failure does not revoke login");
                }
                else
                {
                    Check(!authWindow.LoggedInForChecks && authWindow.LoginEnabledForChecks && !authWindow.ReviewEnabledForChecks
                        && authWindow.ItemsForChecks == 0 && authWindow.AccountForChecks == "尚未登录审核账号"
                        && authWindow.ScheduleForChecks.Contains("已暂停") && authWindow.StatusForChecks.Contains(mode is "role" or "list403" ? "没有审核权限" : "登录已失效"),
                        mode + ": expired session synchronizes label, login button, preview, list and polling state");
                    var calls = authServer.Requests.Count;
                    await authWindow.CheckForChecksAsync();
                    Check(authServer.Requests.Count == calls, mode + ": logged-out checks send no requests");
                }
                Check(authServer.Decisions == (mode == "committed401" ? 1 : 0), mode + ": no automatic replay of approval");
                authServer.AnonymousMe = authServer.RoleRemoved = authServer.Offline = authServer.ImageUnauthorized = authServer.ExpireAfterDecision = false;
                authServer.ReviewStatus = authServer.MutationStatus = null;
                if (mode != "committed401")
                {
                    await authWindow.LoginForChecksAsync();
                    Check(authWindow.LoggedInForChecks && authWindow.ItemsForChecks == 1 && authWindow.AccountForChecks.Contains("审核员")
                        && !authWindow.ContentTextForChecks.Contains("请登录"),
                        mode + ": manual login recovers checks without restarting");
                }
                authWindow.Hide();
            }

            SynchronizationContext.SetSynchronizationContext(null);
            var checkedAt = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
            var settings = new ReviewSettings { LastCheckUtc = checkedAt };
            Check(settings.NextCheckUtc - checkedAt == TimeSpan.FromMinutes(1), "Server polling interval is one minute");
            Check(checkedAt.AddSeconds(59) < settings.NextCheckUtc, "Automatic API polling remains paused before the interval");
            Check(checkedAt.AddSeconds(60) >= settings.NextCheckUtc, "Automatic API polling becomes due after one minute");
            Check(ReviewSettings.StartupCommand().EndsWith("\" --background"), "Startup command uses the quoted local executable");

            var server = new Server();
            using var api = new ReviewApi(server, new Uri("https://market.example/"));
            var displayName = await api.LoginAsync("reviewer", "synthetic-password", CancellationToken.None);
            Check(displayName == "审核员", "Login accepts an authenticated market reviewer");
            var items = await api.ListAsync(CancellationToken.None);
            Check(items.Count == 1 && items[0].State == "pending", "Review list contains only pending market submissions");
            var snapshot = await api.InspectAsync(items[0], CancellationToken.None);
            Check(snapshot.Supported && snapshot.Text.Contains("合成排序") && snapshot.Images.Count == 0,
                "Submission metadata is rendered as text without executing it");
            await api.DecideAsync(snapshot, approve: true, "", CancellationToken.None);
            Check(server.Decisions == 1 && server.LastDecision?.Approve == true,
                "Approval is submitted to the market server after rechecking the revision");
            Check(server.Requests.All(uri => uri.Host == "market.example"), "Credentials and review requests stay on the configured HTTPS origin");
            var notifications = new NotificationState();
            Check(notifications.Update(items).Count == 1 && notifications.Update(items).Count == 0
                && notifications.Update([items[0] with { ReviewToken = new string('b', 64) }]).Count == 1,
                "Notifications track full submission changes without repeated unchanged alerts");

            foreach (var broken in new[] { false, true })
            {
                var imageServer = new Server(withImage: true, tamperedImage: broken);
                using var imageApi = new ReviewApi(imageServer, new Uri("https://market.example/"));
                await imageApi.LoginAsync("reviewer", "synthetic-password", default);
                var imageSnapshot = await imageApi.InspectAsync((await imageApi.ListAsync(default)).Single(), default);
                Check(imageSnapshot.Supported != broken && imageSnapshot.Images.Count == (broken ? 0 : 1),
                    broken ? "Tampered image disables approval without executing content" : "Image bytes are read and verified against their advertised digest");
                await imageApi.DecideAsync(imageSnapshot, approve: false, "请修改图片或说明", default);
                Check(imageServer.LastDecision?.Approve == false, "Reviewer can return a submission including one with broken images");
                await imageApi.LogoutAsync(default);
                Check(imageServer.Requests.Last().AbsolutePath == "/api/logout", "Logout revokes the server session on demand");
            }

            using (var nonAdmin = new ReviewApi(new Server(admin: false), new Uri("https://market.example/")))
            {
                bool rejected = false;
                try { await nonAdmin.LoginAsync("reviewer", "synthetic-password", CancellationToken.None); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("没有审核权限")) { rejected = true; }
                Check(rejected, "Non-reviewer accounts cannot enter the review workflow");
            }

            var staleServer = new Server(stale: true);
            using (var staleApi = new ReviewApi(staleServer, new Uri("https://market.example/")))
            {
                await staleApi.LoginAsync("reviewer", "synthetic-password", CancellationToken.None);
                var staleItem = (await staleApi.ListAsync(CancellationToken.None)).Single();
                var stale = await staleApi.InspectAsync(staleItem, CancellationToken.None);
                bool rejected = false;
                try { await staleApi.DecideAsync(stale, approve: true, "", CancellationToken.None); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("已变化")) { rejected = true; }
                Check(rejected && staleServer.Decisions == 0, "Changed submissions cannot be approved from a stale review snapshot");
            }

            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"),
                JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), ex.ToString());
            return 1;
        }
    }

    private sealed class Server(bool admin = true, bool stale = false, bool withImage = false, bool tamperedImage = false, bool failAfterDecision = false) : HttpMessageHandler
    {
        private const string SubmissionId = "11111111111141118111111111111111";
        private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string ImageId = "22222222222242228222222222222222";
        private readonly byte[] image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        internal readonly List<Uri> Requests = [];
        internal int Decisions;
        internal Decision? LastDecision;
        internal bool AnonymousMe, RoleRemoved, Offline, ImageUnauthorized, ExpireAfterDecision;
        internal HttpStatusCode? ReviewStatus, MutationStatus;
        private bool approved;
        private int reviewReads;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (Offline) throw new HttpRequestException("Synthetic offline");
            object? body = null;
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length > 16 * 1024) throw new InvalidOperationException("Review request body exceeded the mock limit.");
                body = JsonSerializer.Deserialize<JsonElement>(bytes);
            }

            if (uri.AbsolutePath == "/api/csrf" && request.Method == HttpMethod.Get)
                return Json(new { token = "synthetic-csrf-token" });
            if (uri.AbsolutePath == "/api/login" && request.Method == HttpMethod.Post)
            {
                CheckCsrf(request);
                return NoContent();
            }
            if (uri.AbsolutePath == "/api/me" && request.Method == HttpMethod.Get)
                return ExpireAfterDecision && Decisions > 0 ? Error(HttpStatusCode.Unauthorized, "请先登录 RimWise 账号。")
                    : AnonymousMe ? Json(null!) : Json(new { username = "reviewer", nickname = "审核员", admin = admin && !RoleRemoved });
            if (uri.AbsolutePath == "/api/review" && request.Method == HttpMethod.Get)
            {
                if (failAfterDecision && Decisions > 0) throw new HttpRequestException("Synthetic refresh failure");
                if (ReviewStatus is { } status) return Error(status, "请先登录 RimWise 账号。");
                return Json(approved ? Array.Empty<object>() : new[] { Item(stale && ++reviewReads >= 3 ? new string('b', 64) : Digest) });
            }
            if (uri.AbsolutePath == "/api/images/" + ImageId)
                return ImageUnauthorized ? Error(HttpStatusCode.Unauthorized, "请先登录 RimWise 账号。")
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tamperedImage ? new byte[] { 1, 2, 3 } : image) };
            if (uri.AbsolutePath == "/api/review/" + SubmissionId && request.Method == HttpMethod.Post)
            {
                CheckCsrf(request);
                if (MutationStatus is { } status) return Error(status, "页面验证已失效，请刷新后重试。");
                if (stale) return Error(HttpStatusCode.Conflict, "投稿状态或内容已变化，请刷新并重新查看后再审核。");
                var payload = (JsonElement)body!;
                var decision = new Decision(payload.GetProperty("approve").GetBoolean(), payload.GetProperty("reason").GetString() ?? "");
                if (!decision.Approve && string.IsNullOrWhiteSpace(decision.Reason)) return Error(HttpStatusCode.BadRequest, "请填写退回原因。");
                LastDecision = decision;
                Decisions++;
                approved = decision.Approve;
                return NoContent();
            }
            if (uri.AbsolutePath == "/api/logout" && request.Method == HttpMethod.Post)
            {
                CheckCsrf(request);
                return NoContent();
            }
            throw new InvalidOperationException("Unexpected mock market API request: " + request.Method + " " + uri.AbsolutePath);
        }

        private object Item(string sha) => new
        {
            id = SubmissionId, listingId = (string?)null, version = "1.0.0", state = "pending", sha256 = Digest, reviewToken = sha,
            author = "排序作者", ownerUsername = "author", reason = "", metadata = new
            {
                title = "合成排序", description = "仅用于审核助手隔离测试。", gameVersions = "1.7", dlc = "",
                workshopId = (string?)null, releaseNotes = "", modCount = 1
            }, images = withImage ? new[] { new { id = ImageId, mime = "image/png", bytes = image.Length,
                sha256 = RimWise.Integration.Marketplace.MarketplaceJson.ComputeSha256(image) } } : Array.Empty<object>()
        };

        private static void CheckCsrf(HttpRequestMessage request)
        {
            if (!request.Headers.TryGetValues("X-RimWise-CSRF", out var values) || values.Single() != "synthetic-csrf-token"
                || !request.Headers.TryGetValues("Origin", out var origins) || origins.Single() != "https://market.example")
                throw new InvalidOperationException("Market mutation omitted the expected origin or CSRF header.");
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        private static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);
        private static HttpResponseMessage Error(HttpStatusCode code, string message) => new(code)
        { Content = new StringContent(JsonSerializer.Serialize(new { error = message }), Encoding.UTF8, "application/json") };
    }

    private sealed record Decision(bool Approve, string Reason);
}
