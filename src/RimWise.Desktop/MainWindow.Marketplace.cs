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
    private LocalMarketApiClient? localMarketClient;
    private MarketplaceAuthorSession? marketplaceAuthorSession;
    private string? marketplaceNoticeAcknowledgedProfile;
    private bool marketplaceNoticeShowing;
    private bool marketplaceFilePreparing;
    private MarketplaceAuthorSession MarketplaceAuthor => marketplaceAuthorSession ??= new(UserStorage.DirectoryPath);
    private LocalMarketApiClient LocalMarket => localMarketClient ??= new();

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
            ConfigureMarketplacePage(new MarketplacePageActions
            {
                LoadCatalogueAsync = token => LocalMarket.GetCatalogueAsync(token),
                LoadManifestAsync = (entry, token) => LocalMarket.GetManifestAsync(entry, token),
                LoadManifestWithProgressAsync = (entry, progress, token) => LocalMarket.GetManifestAsync(entry, token),
                DownloadAsync = DownloadMarketplaceOrderAsync,
                NotifyDownload = ShowMarketplaceDownloadNotification,
                PublishCurrent = PublishMarketplaceOrder,
                PublishFileAsync = PublishMarketplaceFileAsync,
                OpenTrustedLink = OpenMarketplaceWorkshop,
                RefreshUserAsync = token => LocalMarket.RefreshUserAsync(token),
                LoginAsync = LoginToLocalMarketAsync,
                LogoutAsync = token => LocalMarket.LogoutAsync(token),
                CurrentUser = () => LocalMarket.User,
                ManageAccount = ManageLocalMarketAccount,
                LoadMyOrdersAsync = token => LocalMarket.GetMineAsync(token),
                LoadImageAsync = (image, token) => LocalMarket.LoadImageAsync(image, token),
                UpdateOrder = entry => PublishMarketplaceOrderCore(entry),
                DeleteDraft = saved => new MarketplaceDraftStore(UserStorage.DirectoryPath).Delete(saved.Key),
                DeleteOrderAsync = (entry, scope, token) => LocalMarket.WithdrawOrderAsync(entry, scope, token),
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

    private async Task PublishMarketplaceFileAsync()
    {
        if (marketplaceFilePreparing) return;
        marketplaceFilePreparing = true;
        try
        {
            // Select and read a separate snapshot before opening the editor. Never use
            // ImportOrderAsync/LoadFile here: publishing must not replace the game order.
            var dialog = new OpenFileDialog
            {
                Title = "选择要发布的其他排序文件", Filter = OrderImportDialog.FallbackFilter,
                CheckFileExists = true, Multiselect = false, AddToRecent = false
            };
            if (dialog.ShowDialog(this) != true) return;
            var draft = await LoadMarketplaceFileDraftAsync(dialog.FileName, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            PublishMarketplaceOrderCore(null, fileDraft: draft, sourceName: Path.GetFileName(dialog.FileName));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Xml.XmlException or System.Text.Json.JsonException
            or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            MessageDialog.Show(this, "无法读取所选排序文件，请确认文件格式及内容有效。当前排序未被修改。\n" + ex.Message,
                "尚不能发布排序文件", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { marketplaceFilePreparing = false; }
    }

    internal async Task<MarketplaceManifest> LoadMarketplaceFileDraftAsync(string file, CancellationToken token)
    {
        var catalogue = ViewModel.CurrentCatalogue;
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var source = orderFiles.Read(file);
            token.ThrowIfCancellationRequested();
            return CreateMarketplaceDraft(source, GameVersion.Parse(source.Configuration.GameVersion), catalogue,
                Path.GetFileNameWithoutExtension(file), preferFileReferences: true);
        }, token);
    }

    private async Task<GitHubMarketplaceUser> LoginToLocalMarketAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dialog = new LocalMarketAccountWindow(LocalMarket) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.AuthenticatedUser is not { } signedIn)
            throw new OperationCanceledException(token);
        return signedIn;
    }

    private void ManageLocalMarketAccount()
    {
        if (LocalMarket.User is not { } user) return;
        new LocalMarketAccountSettingsWindow(LocalMarket, user) { Owner = this }.ShowDialog();
    }

    private async void PublishMarketplaceOrderCore(MarketplaceEntry? update, MarketplaceSavedDraft? saved = null,
        MarketplaceManifest? fileDraft = null, string? sourceName = null)
    {
        try
        {
            var draft = saved?.Manifest ?? fileDraft;
            IReadOnlyList<byte[]>? publishedImages = null;
            if (update is not null)
            {
                if (LocalMarket.User?.Login.Equals(update.Author, StringComparison.OrdinalIgnoreCase) != true)
                    throw new InvalidOperationException("请登录发布该排序的市场账号后更新。");
                var published = (await LocalMarket.GetManifestAsync(update, lifetime.Token)).Value;
                // Server image references are catalogue assets, not GitHub manifest paths.
                // Restore their verified bytes separately so updating metadata never drops the cover.
                var loadedImages = new List<byte[]>();
                foreach (var image in update.Images)
                    loadedImages.Add(await LocalMarket.GetImageAsync(image, lifetime.Token));
                publishedImages = loadedImages;
                bool resubmit = update.PublicationState is "draft" or "rejected";
                draft = resubmit ? published with { RemoteRevisionId = update.ManifestPath }
                    : published with { Version = MarketplaceVersions.Next(published.Version), ReleaseNotes = "" };
            }
            draft ??= CreateMarketplaceDraft();
            lifetime.Token.ThrowIfCancellationRequested();
            var publishingActions = MarketplacePublishWindow.CreateProductionActions(draft, UserStorage.DirectoryPath,
                OpenMarketplaceWorkshop, loadMarketplaceIdsAsync: async token =>
            {
                var catalogue = await LocalMarket.GetCatalogueAsync(token);
                return catalogue.Value.Entries.Select(entry => entry.Id).Distinct().ToArray();
            }, isUpdate: saved?.IsUpdate ?? update is not null, savedDraft: saved, accountMarket: LocalMarket,
                initialImages: publishedImages, initialNotice: update?.PublicationState == "rejected"
                    ? "投稿已退回：" + update.RejectionReason + "。修改后可重新提交审核，保持原版本号。" : null,
                snapshotLabel: sourceName is not null ? "文件排序快照 · " + sourceName + "（不会修改当前排序）"
                    : saved is not null || update is not null ? "排序快照" : "当前使用排序快照");
            new MarketplacePublishWindow(draft, publishingActions) { Owner = this }.ShowDialog();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or MarketplaceException)
        {
            MessageDialog.Show(this, ex.Message, "尚不能发布排序", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    internal static async Task<MarketplaceManifest> LoadMarketplaceUpdateAsync(MarketplaceEntry entry, MarketplaceClient client,
        CancellationToken token)
    {
        var published = (await client.GetManifestAsync(entry, token)).Value;
        return published with { Version = MarketplaceVersions.Next(published.Version), ReleaseNotes = "" };
    }

    internal MarketplaceManifest CreateMarketplaceDraft()
    {
        if (!ViewModel.HasDocument) throw new InvalidDataException("请先载入或导入要发布的排序。");
        return CreateMarketplaceDraft(ViewModel.CreateOrderFile(), ViewModel.EffectiveGameVersion,
            ViewModel.CurrentCatalogue, "我的排序");
    }

    private static MarketplaceManifest CreateMarketplaceDraft(OrderFileDocument source, GameVersion? version,
        ModCatalogue? catalogue, string title, bool preferFileReferences = false)
    {
        var document = source.Configuration;
        if (document.Order.PackageIds.Count == 0) throw new InvalidDataException("所选文件没有可发布的模组排序。");
        var hints = source.WorkshopReferences;
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
                var workshop = preferFileReferences && references.Length == 1 ? references[0]
                    : installed?.Source == ModSource.Workshop ? installed.WorkshopId
                    : references.Length == 1 ? references[0] : null;
                mods.Add(new(id, ImportedOrderDownloads.WorkshopIdFromInput(workshop ?? "")));
            }
        }
        return new MarketplaceManifest(1, MarketplaceJson.GenerateUniqueId([]), "1.0",
            string.IsNullOrWhiteSpace(title) ? "我的排序" : title, "unpublished", "", version?.Series ?? "", dlc.AsReadOnly(), null, mods.AsReadOnly());
    }

    private async Task<IReadOnlyList<string>> LoadMarketplaceIdsAsync(CancellationToken token)
    {
        marketplaceClient ??= new MarketplaceClient(
            Path.Combine(UserStorage.DirectoryPath, "Data", "Marketplace", MarketplaceRepository.Name, "Cache"));
        var catalogue = await marketplaceClient.GetCatalogueAsync(token);
        return catalogue.Value.Entries.Select(entry => entry.Id).ToArray();
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
