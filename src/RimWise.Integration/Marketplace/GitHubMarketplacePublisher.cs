using System.Net;

namespace RimWise.Integration.Marketplace;

/// <summary>Creates pending PRs for new versions or author-requested removal. Never approves or merges.</summary>
public sealed class GitHubMarketplacePublisher : IDisposable
{
    private readonly MarketplaceHttp http;
    private readonly TimeProvider time;

    public GitHubMarketplacePublisher(HttpMessageHandler? handler = null, TimeProvider? timeProvider = null)
    {
        http = new MarketplaceHttp(handler);
        time = timeProvider ?? TimeProvider.System;
    }

    public async Task<GitHubMarketplaceUser> GetAuthenticatedUserAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        MarketplaceHttp.ValidateToken(accessToken);
        using var response = await http.JsonAsync(HttpMethod.Get, "user", accessToken, null, cancellationToken).ConfigureAwait(false);
        var root = response.RootElement;
        var login = GitHubJson.String(root, "login", 39);
        MarketplaceJson.Require(MarketplaceJson.IsLogin(login), "GitHub returned an invalid user login.");
        string? name = null;
        if (root.TryGetProperty("name", out var value) && value.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            MarketplaceJson.Require(value.ValueKind == System.Text.Json.JsonValueKind.String, "Invalid GitHub display name.");
            name = value.GetString();
            MarketplaceJson.Require(name is not null && name.Length <= 256 && !name.Any(char.IsControl), "Invalid GitHub display name.");
        }
        return new(login, name);
    }

    public async Task<MarketplaceSubmission> SubmitAsync(MarketplaceManifest manifest, string accessToken,
        CancellationToken cancellationToken = default)
    {
        return await SubmitWithImagesAsync(manifest, [], accessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MarketplaceSubmission> SubmitWithImagesAsync(MarketplaceManifest manifest,
        IReadOnlyList<MarketplaceImageUpload> images, string accessToken, CancellationToken cancellationToken = default)
    {
        MarketplaceHttp.ValidateToken(accessToken);
        images = images.Select(item => new MarketplaceImageUpload(item.Image, item.Content.ToArray())).ToArray();
        MarketplaceJson.Require(manifest.Images.SequenceEqual(images.Select(item => item.Image)), "投稿图片与排序清单不匹配。");
        foreach (var image in images) MarketplaceImages.Verify(image.Image, image.Content);
        var bytes = MarketplaceJson.SerializeManifest(manifest);
        manifest = MarketplaceJson.ParseManifest(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        var user = await GetAuthenticatedUserAsync(accessToken, cancellationToken).ConfigureAwait(false);
        MarketplaceJson.Require(manifest.Author.Equals(user.Login, StringComparison.OrdinalIgnoreCase),
            "Manifest author must match the authenticated GitHub user; publication was refused.");
        var isOwner = user.Login.Equals(MarketplaceRepository.Owner, StringComparison.OrdinalIgnoreCase);
        var path = MarketplaceJson.GetManifestPath(manifest.Author, manifest.Id, manifest.Version);
        var upstream = "repos/" + MarketplaceHttp.Repository;
        var exists = await http.SendAsync(HttpMethod.Get, MarketplaceHttp.ApiRoot + upstream + "/contents/" + path + "?ref=main",
            accessToken, null, MarketplaceHttp.MaximumApiBytes, cancellationToken, allowNotFound: true).ConfigureAwait(false);
        MarketplaceJson.Require(exists.Status == HttpStatusCode.NotFound,
            "This author/id/version is already published. Choose a new immutable version.");
        string baseSha;
        using (var reference = await http.JsonAsync(HttpMethod.Get, upstream + "/git/ref/heads/main", accessToken,
            null, cancellationToken).ConfigureAwait(false))
        {
            var obj = GitHubJson.Object(reference.RootElement, "object");
            MarketplaceJson.Require(GitHubJson.String(obj, "type", 20) == "commit", "Official main does not reference a commit.");
            baseSha = GitHubJson.Sha(obj, "sha");
        }
        var forkPath = await EnsureForkAsync(user, accessToken, cancellationToken).ConfigureAwait(false);
        // Starting from official main keeps unrelated contributor changes out of the PR.
        var branch = "marketplace/" + manifest.Id + "/" + manifest.Version + "-" + Guid.NewGuid().ToString("N");
        using (var reference = await http.JsonAsync(HttpMethod.Post, forkPath + "/git/refs", accessToken,
            new { @ref = "refs/heads/" + branch, sha = baseSha }, cancellationToken).ConfigureAwait(false))
        {
            MarketplaceJson.Require(GitHubJson.String(reference.RootElement, "ref", 256) == "refs/heads/" + branch &&
                GitHubJson.Sha(GitHubJson.Object(reference.RootElement, "object"), "sha") == baseSha,
                "GitHub did not confirm the requested submission branch.");
        }
        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var uploaded = await http.JsonAsync(HttpMethod.Put, forkPath + "/contents/" + image.Image.Path, accessToken,
                new { message = $"Add marketplace image {manifest.Id} {manifest.Version}", content = Convert.ToBase64String(image.Content), branch }, cancellationToken).ConfigureAwait(false);
            MarketplaceJson.Require(GitHubJson.String(GitHubJson.Object(uploaded.RootElement, "content"), "path", 512) == image.Image.Path, "GitHub 未确认图片上传完成。");
        }
        // Omitting 'sha' makes this a create-only Contents API operation: no existing version can be overwritten.
        using (var created = await http.JsonAsync(HttpMethod.Put, forkPath + "/contents/" + path, accessToken,
            new { message = $"Submit marketplace {manifest.Id} {manifest.Version}", content = Convert.ToBase64String(bytes), branch },
            cancellationToken).ConfigureAwait(false))
        {
            MarketplaceJson.Require(GitHubJson.String(GitHubJson.Object(created.RootElement, "content"), "path", 512) == path,
                "GitHub did not confirm the new manifest path.");
        }
        var hash = MarketplaceJson.ComputeSha256(bytes);
        using var pull = await http.JsonAsync(HttpMethod.Post, upstream + "/pulls", accessToken, new
        {
            title = $"Marketplace: {manifest.Id} {manifest.Version}",
            head = isOwner ? branch : user.Login + ":" + branch,
            @base = "main",
            body = $"Pending marketplace submission for maintainer review.\n\nManifest: `{path}`\nSHA-256: `{hash}`\n\nThis submission contains a new manifest and its declared images only. The trusted maintainer workflow must validate and update the official catalogue after review.",
            maintainer_can_modify = true
        }, cancellationToken).ConfigureAwait(false);
        return ValidateSubmission(pull.RootElement, user, branch, path, hash);
    }

    private static MarketplaceSubmission ValidateSubmission(System.Text.Json.JsonElement root, GitHubMarketplaceUser user,
        string branch, string path, string hash)
    {
        var number = GitHubJson.Integer(root, "number", 1, int.MaxValue);
        var url = GitHubJson.String(root, "html_url", 256);
        var baseObject = GitHubJson.Object(root, "base");
        var headObject = GitHubJson.Object(root, "head");
        MarketplaceJson.Require(url == $"https://github.com/{MarketplaceHttp.Repository}/pull/{number}" &&
            GitHubJson.String(root, "state", 20) == "open" &&
            root.TryGetProperty("merged_at", out var mergedAt) && mergedAt.ValueKind == System.Text.Json.JsonValueKind.Null &&
            (!root.TryGetProperty("merged", out var merged) || merged.ValueKind == System.Text.Json.JsonValueKind.False) &&
            GitHubJson.String(baseObject, "ref", 256) == "main" &&
            GitHubJson.String(GitHubJson.Object(baseObject, "repo"), "full_name", 128) == MarketplaceHttp.Repository &&
            GitHubJson.String(headObject, "ref", 256) == branch &&
            GitHubJson.String(GitHubJson.Object(headObject, "repo"), "full_name", 128).Equals(user.Login + "/" + MarketplaceRepository.Name, StringComparison.OrdinalIgnoreCase),
            "GitHub did not confirm an open, unmerged submission PR.");
        return new(number, new Uri(url), branch, path, hash);
    }

    private async Task<string> EnsureForkAsync(GitHubMarketplaceUser user, string accessToken, CancellationToken cancellationToken)
    {
        var upstream = "repos/" + MarketplaceHttp.Repository;
        var isOwner = user.Login.Equals(MarketplaceRepository.Owner, StringComparison.OrdinalIgnoreCase);
        var forkPath = isOwner ? upstream : "repos/" + user.Login + "/" + MarketplaceRepository.Name;
        if (!isOwner)
        {
            var existingFork = await http.SendAsync(HttpMethod.Get, MarketplaceHttp.ApiRoot + forkPath, accessToken, null,
                MarketplaceHttp.MaximumApiBytes, cancellationToken, allowNotFound: true).ConfigureAwait(false);
            if (existingFork.Status == HttpStatusCode.NotFound)
            {
                using var created = await http.JsonAsync(HttpMethod.Post, upstream + "/forks", accessToken,
                    new { name = MarketplaceRepository.Name, default_branch_only = true }, cancellationToken).ConfigureAwait(false);
                ValidateFork(created.RootElement, user.Login, requireParent: false);
                var ready = false;
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var probe = await http.SendAsync(HttpMethod.Get, MarketplaceHttp.ApiRoot + forkPath, accessToken,
                        null, MarketplaceHttp.MaximumApiBytes, cancellationToken, allowNotFound: true).ConfigureAwait(false);
                    if (probe.Status != HttpStatusCode.NotFound)
                    {
                        using var document = MarketplaceJson.Document(probe.Bytes, MarketplaceHttp.MaximumApiBytes);
                        ValidateFork(document.RootElement, user.Login);
                        ready = true;
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), time, cancellationToken).ConfigureAwait(false);
                }
                MarketplaceJson.Require(ready, "GitHub is still preparing the contributor fork; retry publishing later.");
            }
            else
            {
                using var document = MarketplaceJson.Document(existingFork.Bytes, MarketplaceHttp.MaximumApiBytes);
                ValidateFork(document.RootElement, user.Login);
            }
        }
        return forkPath;
    }

    /// <summary>Proposes deletion of all versions of one author's order. Main is never modified here.</summary>
    public async Task<MarketplaceSubmission> RequestDeletionAsync(MarketplaceEntry entry, string accessToken,
        CancellationToken cancellationToken = default)
    {
        MarketplaceJson.ValidateEntry(entry);
        MarketplaceHttp.ValidateToken(accessToken);
        cancellationToken.ThrowIfCancellationRequested();
        var user = await GetAuthenticatedUserAsync(accessToken, cancellationToken).ConfigureAwait(false);
        MarketplaceJson.Require(entry.Author.Equals(user.Login, StringComparison.OrdinalIgnoreCase), "只能删除当前 GitHub 账号发布的排序。");
        var upstream = "repos/" + MarketplaceHttp.Repository;
        string baseSha;
        using (var reference = await http.JsonAsync(HttpMethod.Get, upstream + "/git/ref/heads/main", accessToken, null, cancellationToken).ConfigureAwait(false))
        {
            var obj = GitHubJson.Object(reference.RootElement, "object");
            MarketplaceJson.Require(GitHubJson.String(obj, "type", 20) == "commit", "市场主分支引用无效。");
            baseSha = GitHubJson.Sha(obj, "sha");
        }
        string treeSha;
        using (var commit = await http.JsonAsync(HttpMethod.Get, upstream + "/git/commits/" + baseSha, accessToken, null, cancellationToken).ConfigureAwait(false))
        {
            MarketplaceJson.Require(GitHubJson.Sha(commit.RootElement, "sha") == baseSha, "市场提交校验失败。");
            treeSha = GitHubJson.Sha(GitHubJson.Object(commit.RootElement, "tree"), "sha");
        }
        var prefix = entry.ManifestPath[..(entry.ManifestPath.LastIndexOf('/') + 1)];
        var paths = new HashSet<string>(StringComparer.Ordinal);
        using (var tree = await http.JsonAsync(HttpMethod.Get, upstream + "/git/trees/" + treeSha + "?recursive=1", accessToken, null, cancellationToken).ConfigureAwait(false))
        {
            var root = tree.RootElement;
            MarketplaceJson.Require(GitHubJson.Sha(root, "sha") == treeSha && root.TryGetProperty("truncated", out var truncated)
                && truncated.ValueKind == System.Text.Json.JsonValueKind.False && root.TryGetProperty("tree", out var values)
                && values.ValueKind == System.Text.Json.JsonValueKind.Array, "市场文件列表不完整，未提交删除。");
            foreach (var item in root.GetProperty("tree").EnumerateArray())
            {
                var path = GitHubJson.String(item, "path", 512);
                var imagePrefix = $"marketplace/images/{entry.Author.ToLowerInvariant()}/{entry.Id}/";
                if (path.StartsWith(imagePrefix, StringComparison.Ordinal) && GitHubJson.String(item, "type", 20) == "blob")
                { MarketplaceJson.Require(GitHubJson.String(item, "mode", 20) == "100644" && paths.Add(path), "图片删除范围无效。"); continue; }
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var versionFile = path[prefix.Length..];
                MarketplaceJson.Require(versionFile.EndsWith(".json", StringComparison.Ordinal)
                    && MarketplaceJson.GetManifestPath(entry.Author, entry.Id, versionFile[..^5]) == path
                    && GitHubJson.String(item, "type", 20) == "blob" && GitHubJson.String(item, "mode", 20) == "100644"
                    && paths.Add(path) && paths.Count <= MarketplaceJson.MaximumEntries, "待删除排序的文件范围校验失败。");
            }
        }
        MarketplaceJson.Require(paths.Contains(entry.ManifestPath), "此排序已下架或目录已变化，请刷新后重试。");
        var forkPath = await EnsureForkAsync(user, accessToken, cancellationToken).ConfigureAwait(false);
        string removedTree;
        using (var created = await http.JsonAsync(HttpMethod.Post, forkPath + "/git/trees", accessToken,
            new { base_tree = treeSha, tree = paths.Order(StringComparer.Ordinal).Select(path => new { path, mode = "100644", type = "blob", sha = (string?)null }).ToArray() }, cancellationToken).ConfigureAwait(false))
            removedTree = GitHubJson.Sha(created.RootElement, "sha");
        string removedCommit;
        using (var created = await http.JsonAsync(HttpMethod.Post, forkPath + "/git/commits", accessToken,
            new { message = "Request marketplace removal: " + entry.Id, tree = removedTree, parents = new[] { baseSha } }, cancellationToken).ConfigureAwait(false))
        {
            removedCommit = GitHubJson.Sha(created.RootElement, "sha");
            MarketplaceJson.Require(GitHubJson.Sha(GitHubJson.Object(created.RootElement, "tree"), "sha") == removedTree,
                "GitHub 未确认删除提交的文件树。");
            var parents = created.RootElement.GetProperty("parents");
            MarketplaceJson.Require(parents.GetArrayLength() == 1 && GitHubJson.Sha(parents[0], "sha") == baseSha, "删除提交的父版本无效。");
        }
        var branch = "marketplace/remove/" + entry.Id + "-" + Guid.NewGuid().ToString("N");
        using (var reference = await http.JsonAsync(HttpMethod.Post, forkPath + "/git/refs", accessToken,
            new { @ref = "refs/heads/" + branch, sha = removedCommit }, cancellationToken).ConfigureAwait(false))
            MarketplaceJson.Require(GitHubJson.String(reference.RootElement, "ref", 256) == "refs/heads/" + branch
                && GitHubJson.Sha(GitHubJson.Object(reference.RootElement, "object"), "sha") == removedCommit, "GitHub 未确认删除审核分支。");
        using var pull = await http.JsonAsync(HttpMethod.Post, upstream + "/pulls", accessToken, new
        {
            title = "申请下架排序：" + entry.Id,
            head = user.Login.Equals(MarketplaceRepository.Owner, StringComparison.OrdinalIgnoreCase) ? branch : user.Login + ":" + branch,
            @base = "main", body = "作者申请删除此排序的全部已发布版本，审核合并后重建市场目录。\n\n排序标识：" + entry.Id + "\n版本数量：" + paths.Count,
            maintainer_can_modify = true
        }, cancellationToken).ConfigureAwait(false);
        return ValidateSubmission(pull.RootElement, user, branch, entry.ManifestPath, entry.Sha256);
    }

    private static void ValidateFork(System.Text.Json.JsonElement root, string login, bool requireParent = true)
    {
        MarketplaceJson.Require(root.TryGetProperty("fork", out var fork) && fork.ValueKind == System.Text.Json.JsonValueKind.True &&
            root.TryGetProperty("private", out var isPrivate) && isPrivate.ValueKind == System.Text.Json.JsonValueKind.False &&
            GitHubJson.String(root, "full_name", 128).Equals(login + "/" + MarketplaceRepository.Name, StringComparison.OrdinalIgnoreCase) &&
            GitHubJson.String(GitHubJson.Object(root, "owner"), "login", 39).Equals(login, StringComparison.OrdinalIgnoreCase) &&
            (!requireParent && !root.TryGetProperty("parent", out _) ||
                GitHubJson.String(GitHubJson.Object(root, "parent"), "full_name", 128) == MarketplaceHttp.Repository),
            "The contributor repository is not a verified public fork of the official marketplace repository.");
    }

    public void Dispose() => http.Dispose();
}
