using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using RimWise.Core;
using RimWise.Desktop.Views;
using RimWise.Desktop.Services;
using RimWise.Formats;
using RimWise.Integration.Marketplace;
using RimWise.Integration.Steam;
using RimWise.Integration.Scanning;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private MarketplacePage? marketplacePage;
    private MarketplaceClient? marketplaceClient;
    private MarketplaceAuthorSession? marketplaceAuthorSession;
    private string? marketplaceNoticeAcknowledgedProfile;
    private bool marketplaceNoticeShowing;
    private MarketplaceAuthorSession MarketplaceAuthor => marketplaceAuthorSession ??= new(UserStorage.DirectoryPath);

    private bool EnsureMarketplaceNotice()
    {
        if (marketplaceNoticeShowing) return false;
        var profile = UserStorage.DirectoryPath;
        if (marketplaceNoticeAcknowledgedProfile == profile) return true;
        var store = new MarketplaceNoticeStore(profile);
        if (store.IsAcknowledged()) { marketplaceNoticeAcknowledgedProfile = profile; return true; }
        marketplaceNoticeShowing = true;
        try
        {
            if (!MarketplaceFirstUseNotice.Show(this)) return false;
            marketplaceNoticeAcknowledgedProfile = profile;
            try { store.Acknowledge(); }
            catch (Exception ex) when (MarketplaceNoticeStore.IsStorageError(ex))
            {
                ViewModel.Status = "提醒确认记录未能保存，下次启动后会再次显示。";
            }
            return true;
        }
        finally { marketplaceNoticeShowing = false; }
    }

    private void EnsureMarketplacePage()
    {
        if (marketplacePage is null)
        {
            marketplaceClient = new MarketplaceClient(
                Path.Combine(UserStorage.DirectoryPath, "Data", "Marketplace", MarketplaceRepository.Name, "Cache"));
            ConfigureMarketplacePage(new MarketplacePageActions
            {
                LoadCatalogueAsync = token => marketplaceClient.GetCatalogueAsync(token),
                LoadManifestAsync = (entry, token) => marketplaceClient.GetManifestAsync(entry, token),
                LoadManifestWithProgressAsync = (entry, progress, token) => marketplaceClient.GetManifestAsync(entry, token, progress),
                DownloadAsync = DownloadMarketplaceOrderAsync,
                NotifyDownload = ShowMarketplaceDownloadNotification,
                PublishCurrent = PublishMarketplaceOrder,
                OpenTrustedLink = OpenMarketplaceWorkshop,
                RefreshUserAsync = token => MarketplaceAuthor.RefreshAsync(token),
                LoginAsync = token => MarketplaceAuthor.LoginAsync(uri =>
                {
                    if (uri.Scheme != "https" || uri.Host != "github.com" || uri.AbsolutePath != "/login/oauth/authorize")
                        throw new InvalidOperationException("GitHub 授权地址无效。");
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                }, token),
                Logout = () => MarketplaceAuthor.Logout(),
                CurrentUser = () => MarketplaceAuthor.User,
                UpdateOrder = entry => PublishMarketplaceOrderCore(entry),
                DeleteDraft = saved => new MarketplaceDraftStore(UserStorage.DirectoryPath).Delete(saved.Key),
                DeleteOrderAsync = (entry, scope, token) => MarketplaceAuthor.RequestDeletionAsync(entry, scope, token),
                LoadDrafts = () => new MarketplaceDraftStore(UserStorage.DirectoryPath).ReadAll(),
                OpenDraft = saved => PublishMarketplaceOrderCore(null, saved)
            });
        }
        _ = marketplacePage!.EnsureLoadedAsync();
    }

    // Synthetic UI checks inject transports here before selecting the tab. Production
    // has one lazily created page and performs no market requests at application startup.
    internal void ConfigureMarketplacePage(MarketplacePageActions actions)
    {
        if (marketplacePage is null)
            MarketplaceHost.Content = marketplacePage = new MarketplacePage(actions);
        else marketplacePage.Configure(actions);
    }

    private async Task<MarketplaceDownloadResult> DownloadMarketplaceOrderAsync(MarketplaceManifest manifest, bool apply,
        IProgress<string> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (apply)
        {
            var applied = await ApplyMarketplaceManifestAsync(manifest, progress, token);
            return new(applied, ViewModel.Status);
        }
        var dialog = new SaveFileDialog
        {
            Title = "下载排序并另存为", Filter = "排序文件 (*.rml)|*.rml", AddExtension = true,
            DefaultExt = ".rml", FileName = manifest.Id + "-" + manifest.Version + ".rml",
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) return new(false, "已取消另存排序。");
        token.ThrowIfCancellationRequested();
        // Existing export refuses overwrites and writes a fully prepared file. RML
        // retains the Workshop references needed by the ordinary import workflow.
        var document = MarketplaceOrderCompatibility.ToOrderFile(manifest);
        var target = dialog.FileName;
        if (File.Exists(target)) throw new IOException("目标文件已存在，请选择新文件名，已有排序未被覆盖。");
        progress.Report("正在保存排序文件…");
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            new OrderFileService().ExportNew(target, document, OrderFileFormat.RmlXml);
        }, token);
        RememberOrderDirectory(target, exported: true);
        return new(true, "排序文件已下载并另存，可通过文件菜单导入。");
    }

    private void PublishMarketplaceOrder() => PublishMarketplaceOrderCore(null);

    private async void PublishMarketplaceOrderCore(MarketplaceEntry? update, MarketplaceSavedDraft? saved = null)
    {
        try
        {
            var draft = saved?.Manifest;
            if (update is not null)
            {
                if (MarketplaceAuthor.User?.Login.Equals(update.Author, StringComparison.OrdinalIgnoreCase) != true)
                    throw new InvalidOperationException("请登录排序作者的 GitHub 账号后更新。");
                marketplaceClient ??= new MarketplaceClient(
                    Path.Combine(UserStorage.DirectoryPath, "Data", "Marketplace", MarketplaceRepository.Name, "Cache"));
                draft = await LoadMarketplaceUpdateAsync(update, marketplaceClient, CancellationToken.None);
            }
            draft ??= CreateMarketplaceDraft();
            var capturedPaths = paths;
            var pendingPaths = pathInitialization is { IsCompleted: false } ? pathInitialization : null;
            new MarketplacePublishWindow(draft, MarketplaceAuthor, async (candidate, token) =>
            {
                if (pendingPaths is not null)
                {
                    await pendingPaths.WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    capturedPaths = paths;
                    pendingPaths = null;
                }
                return await PrepareMarketplacePublicationAsync(candidate, capturedPaths, token);
            }, LoadMarketplaceIdsAsync, isUpdate: saved?.IsUpdate ?? update is not null, savedDraft: saved)
                { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or MarketplaceException)
        {
            MessageDialog.Show(this, ex.Message, "尚不能发布排序", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    internal static async Task<MarketplaceManifest> LoadMarketplaceUpdateAsync(MarketplaceEntry entry, MarketplaceClient client,
        CancellationToken token)
    {
        var published = (await client.GetManifestAsync(entry, token)).Value;
        var components = published.Version.Split('-')[0].Split('.').Select(int.Parse).ToArray();
        return published with { Version = $"{components[0]}.{components[1]}.{checked(components[2] + 1)}", ReleaseNotes = "" };
    }

    internal MarketplaceManifest CreateMarketplaceDraft()
    {
        if (!ViewModel.HasDocument) throw new InvalidDataException("请先载入或导入要发布的排序。");
        var catalogue = ViewModel.CurrentCatalogue;
        var document = ViewModel.CreateExportDocument();
        var version = ViewModel.EffectiveGameVersion;
        var hints = ViewModel.CreateOrderFile().WorkshopReferences;
        var mods = new List<MarketplaceMod>();
        var dlc = new List<string>();
        foreach (var id in document.Order.PackageIds)
        {
            if (GameContentPolicy.KeepWhenClearing(id, version))
            {
                mods.Add(new(id, null));
                if (!id.Equals("ludeon.rimworld", StringComparison.OrdinalIgnoreCase)) dlc.Add(id);
            }
            else
            {
                var installed = catalogue?.Resolve(id, version).UniqueInstallation;
                var references = hints.Where(hint => hint.Identifier.Equals(id, StringComparison.OrdinalIgnoreCase))
                    .Select(hint => hint.WorkshopId).Distinct().Take(2).ToArray();
                var workshop = installed?.Source == ModSource.Workshop ? installed.WorkshopId
                    : references.Length == 1 ? references[0] : null;
                mods.Add(new(id, ImportedOrderDownloads.WorkshopIdFromInput(workshop ?? "")));
            }
        }
        return new MarketplaceManifest(1, MarketplaceJson.GenerateUniqueId([]), "1.0.0",
            "我的排序", "unpublished", "", version?.Series ?? "", dlc.AsReadOnly(), null, mods.AsReadOnly());
    }

    private async Task<IReadOnlyList<string>> LoadMarketplaceIdsAsync(CancellationToken token)
    {
        marketplaceClient ??= new MarketplaceClient(
            Path.Combine(UserStorage.DirectoryPath, "Data", "Marketplace", MarketplaceRepository.Name, "Cache"));
        var catalogue = await marketplaceClient.GetCatalogueAsync(token);
        return catalogue.Value.Entries.Select(entry => entry.Id).ToArray();
    }

    internal static async Task<MarketplaceManifest> PrepareMarketplacePublicationAsync(MarketplaceManifest draft,
        GamePaths capturedPaths, CancellationToken token)
    {
        var versions = MarketplaceJson.ParseGameVersions(draft.GameVersion).Select(value => GameVersion.Parse(value)!).ToArray();
        var version = versions[0];
        // Scan metadata without inspecting mod content, then verify the captured order.
        // Never refresh or replace the editor.
        var scan = await Task.Run(() => new ModScanner().Scan(capturedPaths, token,
            targetOverride: version, inspectContent: false), token);
        token.ThrowIfCancellationRequested();
        if (scan.Diagnostics.Any(issue => issue.Code is "ScanLimit" or "ScanRootUnreadable" or "NoScanPaths"))
            throw new InvalidDataException("模组目录未完整读取，请检查目录设置后重试；草稿已保留。");
        var resolved = draft.Mods.ToDictionary(mod => mod.PackageId, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
        foreach (var target in versions)
        {
            foreach (var mod in draft.Mods)
            {
                if (GameContentPolicy.KeepWhenClearing(mod.PackageId, target)) continue;
                var installed = scan.Catalogue.Resolve(mod.PackageId, target).UniqueInstallation;
                if (installed is null || installed.Source != ModSource.Workshop
                    || ImportedOrderDownloads.WorkshopIdFromInput(installed.WorkshopId ?? "") is not { } workshop)
                    throw new InvalidDataException($"无法确认 {target.Series} 版本下模组的工坊来源，请检查安装、重复模组或兼容版本：{mod.PackageId}");
                resolved[mod.PackageId].Add(workshop);
            }
        }
        var mods = new List<MarketplaceMod>();
        foreach (var mod in draft.Mods)
        {
            if (versions.All(target => GameContentPolicy.KeepWhenClearing(mod.PackageId, target))) { mods.Add(new(mod.PackageId, null)); continue; }
            if (resolved[mod.PackageId].Count != 1)
                throw new InvalidDataException("不同游戏版本下模组的工坊来源不一致，无法安全发布：" + mod.PackageId);
            mods.Add(new(mod.PackageId, resolved[mod.PackageId].Single()));
        }
        return draft with { Mods = mods.AsReadOnly() };
    }

    private void OpenMarketplaceWorkshop(Uri uri)
    {
        if (ImportedOrderDownloads.WorkshopIdFromInput(uri.AbsoluteUri) is not { } id) return;
        try
        {
            Process.Start(new ProcessStartInfo("https://steamcommunity.com/sharedfiles/filedetails/?id=" + id)
            { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { ViewModel.Status = "无法打开创意工坊，可复制链接手动访问。"; }
    }
}
