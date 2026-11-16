using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RimWise.Integration.Marketplace;

namespace RimWise.MarketplaceReview;

/// <summary>GitHub-only review transport. Nothing in a submitted branch is executed.</summary>
internal sealed class ReviewApi : IDisposable
{
    internal const string Repository = MarketplaceRepository.FullName;
    private readonly HttpClient client;
    private string? token;
    internal ReviewApi(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false })
            { Timeout = TimeSpan.FromSeconds(45) };
    }
    internal void SetToken(string? value) => token = value;
    private async Task<byte[]> SendAsync(HttpMethod method, string path, CancellationToken cancel, object? body = null, int max = 2 * 1024 * 1024)
    {
        var uri = new Uri(path.StartsWith("https://", StringComparison.Ordinal) ? path : "https://api.github.com/" + path);
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Host is not ("api.github.com" or "raw.githubusercontent.com"))
            throw new InvalidDataException("已阻止不受信任的下载地址。");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("RimWise-MarketplaceReview/0.1");
        if (uri.Host == "api.github.com")
        {
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("请先登录 GitHub。");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28"); request.Headers.Accept.ParseAdd("application/vnd.github+json");
        }
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri != uri)
            throw new InvalidOperationException($"GitHub 请求未完成（HTTP {(int)response.StatusCode}）。请检查登录、权限或网络，稍后重试。");
        if (response.Content.Headers.ContentLength > max) throw new InvalidDataException("GitHub 返回内容过大。");
        await using var input = await response.Content.ReadAsStreamAsync(cancel); using var output = new MemoryStream();
        byte[] buffer = new byte[16384]; int count;
        while ((count = await input.ReadAsync(buffer, cancel)) > 0)
        { if (output.Length + count > max) throw new InvalidDataException("GitHub 返回内容过大。"); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private async Task<JsonDocument> JsonAsync(string path, CancellationToken cancel, HttpMethod? method = null, object? body = null)
        => JsonDocument.Parse(await SendAsync(method ?? HttpMethod.Get, path, cancel, body), new JsonDocumentOptions { MaxDepth = 32 });

    internal async Task<string> IdentityAsync(CancellationToken cancel)
    {
        using var user = await JsonAsync("user", cancel);
        using var repo = await JsonAsync("repos/" + Repository, cancel);
        if (!repo.RootElement.GetProperty("permissions").GetProperty("push").GetBoolean())
            throw new InvalidOperationException("当前 GitHub 账号没有市场仓库的合并权限，请使用维护者账号登录。");
        return user.RootElement.GetProperty("login").GetString() ?? throw new InvalidDataException("GitHub 登录信息无效。");
    }
    internal async Task<IReadOnlyList<ReviewPull>> ListAsync(CancellationToken cancel)
    {
        var result = new List<ReviewPull>();
        for (int page = 1; page <= 10; page++)
        {
            using var document = await JsonAsync($"repos/{Repository}/pulls?state=open&base=main&sort=updated&direction=desc&per_page=100&page={page}", cancel);
            result.AddRange(document.RootElement.EnumerateArray().Select(ReviewRules.Pull));
            if (document.RootElement.GetArrayLength() < 100) return result;
        }
        throw new InvalidDataException("待审投稿过多，当前列表不完整，请到 GitHub 检查。");
    }
    private async Task<(ReviewPull Pull, string MergeSha, bool Open)> GetPullAsync(int number, CancellationToken cancel)
    {
        if (number <= 0) throw new InvalidDataException("投稿编号无效。");
        using var document = await JsonAsync($"repos/{Repository}/pulls/{number}", cancel); var root = document.RootElement;
        return (ReviewRules.Pull(root), root.GetProperty("merge_commit_sha").GetString() ?? "", root.GetProperty("state").GetString() == "open");
    }
    internal async Task<ReviewSnapshot> InspectAsync(int number, CancellationToken cancel)
    {
        var (pull, _, open) = await GetPullAsync(number, cancel);
        if (!open || pull.Draft || pull.BaseBranch != "main" || !ReviewRules.IsRepository(pull.HeadRepository))
            throw new InvalidOperationException("该投稿已关闭、仍是草稿或不属于市场主分支。");
        var files = new List<ChangedFile>();
        for (int page = 1; page <= 30; page++)
        {
            using var document = await JsonAsync($"repos/{Repository}/pulls/{number}/files?per_page=100&page={page}", cancel);
            foreach (var file in document.RootElement.EnumerateArray()) files.Add(new(file.GetProperty("filename").GetString() ?? "", file.GetProperty("status").GetString() ?? ""));
            if (document.RootElement.GetArrayLength() < 100) break;
            if (page == 30) throw new InvalidDataException("投稿文件列表不完整，无法安全审核。");
        }
        var additions = files.Where(file => file.Status == "added").ToArray();
        var deletions = files.Where(file => file.Status == "removed").ToArray();
        if (files.Count == 0 || additions.Length + deletions.Length != files.Count || additions.Length > 0 && deletions.Length > 0)
            return new(pull, "此投稿包含修改、重命名或混合新增与删除，请到 GitHub 检查。", files, [], false, "不支持此投稿范围，已禁用合并。");
        if (deletions.Length > 0)
        {
            await VerifyRemovalAsync(pull, deletions, cancel);
            var latestRemoval = await GetPullAsync(number, cancel); Same(pull, latestRemoval);
            return new(pull, "作者本人下架排序及关联图片：\r\n\r\n" + string.Join("\r\n", files.Select(f => f.Path)), files, [], true,
                "已核实原发布者账号与所选版本范围。服务器将自动下架；尚未自动完成时可点击“同意并下架”。");
        }
        var manifests = additions.Where(file => ReviewRules.IsManifest(file.Path, pull.Author)).ToArray();
        if (manifests.Length != 1) return new(pull, "请一次审核一份新排序或一个版本。", files, [], false, "投稿清单范围无效。");
        var bytes = await SendAsync(HttpMethod.Get, ReviewRules.Raw(pull.HeadRepository, pull.Sha, manifests[0].Path).AbsoluteUri, cancel, max: MarketplaceJson.MaximumManifestBytes);
        var manifest = MarketplaceJson.ParseManifest(bytes);
        var expected = manifest.Images.Select(image => image.Path).Append(MarketplaceJson.GetManifestPath(manifest.Author, manifest.Id, manifest.Version)).ToHashSet(StringComparer.Ordinal);
        if (!manifest.Author.Equals(pull.Author, StringComparison.OrdinalIgnoreCase) || !expected.SetEquals(files.Select(file => file.Path)))
            throw new InvalidDataException("清单作者、图片或变更位置不匹配，已禁用合并。");
        var images = new List<ReviewImage>();
        foreach (var image in manifest.Images)
        {
            var imageBytes = await SendAsync(HttpMethod.Get, ReviewRules.Raw(pull.HeadRepository, pull.Sha, image.Path).AbsoluteUri, cancel, max: MarketplaceImages.MaximumBytes);
            MarketplaceImages.Verify(image, imageBytes); images.Add(new(image, imageBytes));
        }
        var text = $"标题：{manifest.Title}\r\n发布者：{manifest.Author}\r\n排序标识：{manifest.Id}\r\n排序版本：{manifest.Version}\r\n游戏版本：{manifest.GameVersion}\r\n模组数：{manifest.Mods.Count}\r\n所需 DLC：{string.Join("、", manifest.RequiredDlc)}\r\n工坊合集：{manifest.WorkshopCollectionId ?? "未绑定"}\r\n\r\n简介：\r\n{manifest.Description}\r\n\r\n更新日志：\r\n{manifest.ReleaseNotes}";
        // Recheck after downloading: the displayed snapshot must be pinned to the actual PR head.
        var latest = await GetPullAsync(number, cancel);
        if (latest.Pull.Sha != pull.Sha || latest.Pull.BaseSha != pull.BaseSha || !latest.Open) throw new InvalidOperationException("投稿在读取时已更新，请重新查看后审核。");
        return new(pull, text, files, images, true, "请人工检查图片与文字。点击同意时仍会重新核对提交与可信校验结果。");
    }

    internal async Task MergeAsync(ReviewSnapshot snapshot, CancellationToken cancel)
    {
        if (!snapshot.Supported) throw new InvalidOperationException("此投稿范围无法安全合并。");
        var current = await GetPullAsync(snapshot.Pull.Number, cancel);
        Same(snapshot.Pull, current);
        if (!ReviewRules.IsSha(current.MergeSha)) throw new InvalidOperationException("GitHub 尚未准备好合并结果，或存在冲突，请稍后刷新。");
        await RequireTrustedCheckAsync(current.Pull, cancel);
        var last = await GetPullAsync(snapshot.Pull.Number, cancel); Same(snapshot.Pull, last);
        if (last.MergeSha != current.MergeSha) throw new InvalidOperationException("合并提交已变化，请刷新后重新审核。");
        using var result = await JsonAsync($"repos/{Repository}/pulls/{snapshot.Pull.Number}/merge", cancel, HttpMethod.Put,
            new { sha = snapshot.Pull.Sha, merge_method = "squash", commit_title = (snapshot.IsRemoval ? "作者本人下架排序 #" : "审核上架市场排序 #") + snapshot.Pull.Number });
        if (!result.RootElement.GetProperty("merged").GetBoolean()) throw new InvalidOperationException("GitHub 未确认合并，请刷新核对结果。");
    }
    private static void Same(ReviewPull shown, (ReviewPull Pull, string MergeSha, bool Open) current)
    {
        if (!current.Open || current.Pull.Draft || current.Pull.BaseBranch != "main" || current.Pull.Sha != shown.Sha
            || current.Pull.BaseSha != shown.BaseSha || current.Pull.Author != shown.Author || current.Pull.AuthorId != shown.AuthorId || current.Pull.HeadRepository != shown.HeadRepository)
            throw new InvalidOperationException("投稿内容或主分支已经变化，请刷新并重新查看后再同意。");
    }
    internal Task CheckReadinessAsync(ReviewPull pull, CancellationToken cancel) => RequireTrustedCheckAsync(pull, cancel);

    private async Task VerifyRemovalAsync(ReviewPull pull, IReadOnlyList<ChangedFile> files, CancellationToken cancel)
    {
        var paths = files.Where(file => ReviewRules.IsManifest(file.Path, pull.Author)).Select(file => file.Path).ToArray();
        if (pull.AuthorId <= 0 || paths.Length == 0) throw new InvalidDataException("未能确认下架申请的原发布者。");
        var expected = new HashSet<string>(StringComparer.Ordinal);
        string? order = null;
        foreach (var path in paths)
        {
            var bytes = await SendAsync(HttpMethod.Get, ReviewRules.Raw(Repository, pull.BaseSha, path).AbsoluteUri, cancel, max: MarketplaceJson.MaximumManifestBytes);
            var m = MarketplaceJson.ParseManifest(bytes);
            order ??= m.Id;
            if (m.Id != order || !m.Author.Equals(pull.Author, StringComparison.OrdinalIgnoreCase) || MarketplaceJson.GetManifestPath(m.Author,m.Id,m.Version) != path)
                throw new InvalidDataException("下架只允许原作者删除同一排序的指定版本或全部版本。");
            expected.Add(path); foreach (var image in m.Images) expected.Add(image.Path);
            using var commits = await JsonAsync($"repos/{Repository}/commits?path={Uri.EscapeDataString(path)}&sha={pull.BaseSha}&per_page=100",cancel);
            long? originalAuthor = null;
            foreach (var commit in commits.RootElement.EnumerateArray())
            {
                var sha = commit.GetProperty("sha").GetString() ?? "";
                if (!ReviewRules.IsSha(sha)) throw new InvalidDataException("发布记录无效。");
                using var originals = await JsonAsync($"repos/{Repository}/commits/{sha}/pulls?per_page=100",cancel);
                if (originals.RootElement.GetArrayLength() >= 100) throw new InvalidDataException("发布记录不完整。");
                foreach (var original in originals.RootElement.EnumerateArray())
                {
                    if (original.GetProperty("merged_at").ValueKind == JsonValueKind.Null || original.GetProperty("base").GetProperty("ref").GetString() != "main"
                        || original.GetProperty("base").GetProperty("repo").GetProperty("full_name").GetString() != Repository) continue;
                    int originalNumber = original.GetProperty("number").GetInt32();
                    bool found = false;
                    for (int page=1; page<=30; page++)
                    {
                        using var originalFiles = await JsonAsync($"repos/{Repository}/pulls/{originalNumber}/files?per_page=100&page={page}",cancel);
                        found |= originalFiles.RootElement.EnumerateArray().Any(file => file.GetProperty("filename").GetString() == path && file.GetProperty("status").GetString() == "added");
                        if (originalFiles.RootElement.GetArrayLength() < 100) break;
                        if (page==30) throw new InvalidDataException("发布记录不完整。");
                    }
                    if (found)
                    {
                        long author = original.GetProperty("user").GetProperty("id").GetInt64();
                        if (originalAuthor is not null && originalAuthor != author) throw new InvalidDataException("发布者身份记录不一致。");
                        originalAuthor = author;
                    }
                }
            }
            if (originalAuthor != pull.AuthorId) throw new InvalidDataException("下架者与原发布者不是同一 GitHub 账号，无法下架。");
        }
        if (!expected.SetEquals(files.Select(file=>file.Path))) throw new InvalidDataException("下架申请包含无关文件或遗漏关联图片。");
        using var tree = await JsonAsync($"repos/{Repository}/git/trees/{pull.BaseSha}?recursive=1",cancel);
        if (tree.RootElement.GetProperty("truncated").GetBoolean()) throw new InvalidDataException("版本列表不完整，无法下架。");
        string prefix = $"marketplace/manifests/{pull.Author.ToLowerInvariant()}/{order}/";
        var all = tree.RootElement.GetProperty("tree").EnumerateArray().Where(item=>item.GetProperty("type").GetString()=="blob")
            .Select(item=>item.GetProperty("path").GetString()!).Where(path=>path.StartsWith(prefix,StringComparison.Ordinal));
        var published = all.ToHashSet(StringComparer.Ordinal);
        if (paths.Any(path=>!published.Contains(path))) throw new InvalidDataException("下架申请包含未发布的版本。");
    }

    private async Task RequireTrustedCheckAsync(ReviewPull pull, CancellationToken cancel)
    {
        using var checks = await JsonAsync($"repos/{Repository}/commits/{pull.Sha}/check-runs?per_page=100&filter=latest", cancel);
        var root = checks.RootElement;
        if (root.GetProperty("total_count").GetInt32() > 100) throw new InvalidOperationException("校验列表过多，无法确认可信检查。");
        var pending = false;
        var failed = false;
        var differentBase = false;
        foreach (var check in root.GetProperty("check_runs").EnumerateArray())
        {
            if (check.GetProperty("name").GetString() != "validate-submission" || check.GetProperty("app").GetProperty("id").GetInt32() != 15368
                || check.GetProperty("app").GetProperty("slug").GetString() != "github-actions" || check.GetProperty("head_sha").GetString() != pull.Sha) continue;
            var details = check.GetProperty("details_url").GetString() ?? "";
            var match = Regex.Match(details, @"^https://github\.com/Manhattanca/RimWise-Marketplace/actions/runs/([1-9][0-9]*)/job/[1-9][0-9]*$");
            if (!match.Success) continue;
            using var runDoc = await JsonAsync($"repos/{Repository}/actions/runs/{match.Groups[1].Value}", cancel); var run = runDoc.RootElement;
            if (run.GetProperty("path").GetString() != ".github/workflows/marketplace.yml" || run.GetProperty("event").GetString() != "pull_request"
                || run.GetProperty("check_suite_id").GetInt64() != check.GetProperty("check_suite").GetProperty("id").GetInt64()
                || run.GetProperty("repository").GetProperty("full_name").GetString() != Repository
                || run.GetProperty("head_sha").GetString() != pull.Sha) continue;
            if (check.GetProperty("status").GetString() != "completed") { pending = true; continue; }
            if (check.GetProperty("conclusion").GetString() != "success") { failed = true; continue; }
            // The trusted step name binds CI to the actual PR base. GitHub can omit
            // pull_requests for forks, so that optional array is not an identity proof.
            using var jobs = await JsonAsync($"repos/{Repository}/actions/runs/{match.Groups[1].Value}/jobs?filter=latest&per_page=100", cancel);
            if (jobs.RootElement.GetProperty("total_count").GetInt32() > 100) continue;
            var proof = "Validate data with the trusted base-branch tool (base " + pull.BaseSha + ")";
            foreach (var job in jobs.RootElement.GetProperty("jobs").EnumerateArray())
                if (job.GetProperty("name").GetString() == "validate-submission" && job.GetProperty("conclusion").GetString() == "success"
                    && job.GetProperty("steps").EnumerateArray().Any(step => step.GetProperty("name").GetString() == proof
                        && step.GetProperty("status").GetString() == "completed" && step.GetProperty("conclusion").GetString() == "success")) return;
            differentBase = true;
        }
        if (pending) throw new InvalidOperationException("此投稿正在等待或运行市场校验。完成后重新点击“查看投稿”或“同意并上架”；无需重新提交。");
        if (failed) throw new InvalidOperationException("此投稿的市场校验未通过或已取消。请打开“查看校验”核对原因，修正或重新运行校验后再审核。");
        if (differentBase) throw new InvalidOperationException("已有成功记录未证明当前主分支的可信校验。请在 GitHub 重新运行市场校验，再重新查看投稿。");
        throw new InvalidOperationException("尚未找到此投稿的可信校验记录。请打开“查看校验”确认是否尚未启动或需要维护者批准；校验通过后再重试。");
    }
    public void Dispose() => client.Dispose();
}
