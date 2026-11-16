using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Services;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Marketplace;

namespace RimWise.Desktop.Views;

public sealed record MarketplaceDownloadResult(bool Success, string Message);
public sealed record MarketplaceDownloadNotice(string Title, string Message, bool Busy, double? Percent = null);

public sealed class MarketplacePageActions
{
    public required Func<CancellationToken, Task<MarketplaceResult<MarketplaceCatalogue>>> LoadCatalogueAsync { get; init; }
    public required Func<MarketplaceEntry, CancellationToken, Task<MarketplaceResult<MarketplaceManifest>>> LoadManifestAsync { get; init; }
    public Func<MarketplaceEntry, IProgress<MarketplaceTransferProgress>, CancellationToken, Task<MarketplaceResult<MarketplaceManifest>>>? LoadManifestWithProgressAsync { get; init; }
    public required Func<MarketplaceManifest, bool, IProgress<string>, CancellationToken, Task<MarketplaceDownloadResult>> DownloadAsync { get; init; }
    public Action<MarketplaceDownloadNotice>? NotifyDownload { get; init; }
    public required Action PublishCurrent { get; init; }
    public required Action<Uri> OpenTrustedLink { get; init; }
    public Func<CancellationToken, Task<GitHubMarketplaceUser?>>? RefreshUserAsync { get; init; }
    public Func<CancellationToken, Task<GitHubMarketplaceUser>>? LoginAsync { get; init; }
    public Action? Logout { get; init; }
    public Action<MarketplaceEntry>? UpdateOrder { get; init; }
    public Func<IReadOnlyList<MarketplaceSavedDraft>>? LoadDrafts { get; init; }
    public Action<MarketplaceSavedDraft>? DeleteDraft { get; init; }
    public Func<MarketplaceEntry, CancellationToken, Task<MarketplaceSubmission>>? DeleteOrderAsync { get; init; }
    public Action<MarketplaceSavedDraft>? OpenDraft { get; init; }
    public Func<GitHubMarketplaceUser?>? CurrentUser { get; init; }
}

public partial class MarketplacePage : UserControl
{
    private MarketplacePageActions? actions;
    private CancellationTokenSource? imageLoading;
    private IReadOnlyList<MarketplaceCard> cards = [];
    private readonly Dictionary<(string Author, string Id, string Version, string Hash), MarketplaceResult<MarketplaceManifest>> manifests = [];
    private MarketplaceCard? selected;
    private MarketplaceManifest? manifest;
    private CancellationTokenSource? active;
    private readonly HashSet<CancellationTokenSource> operations = [];
    private bool changingResults;
    private bool attemptedLoad;
    private bool downloading;
    private bool catalogueFromCache;
    private GitHubMarketplaceUser? user;
    private long cancellationRevision;
    public Task PendingOperation { get; private set; } = Task.CompletedTask;
    public MarketplaceEntry? SelectedEntry => selected?.Entry;
    public MarketplaceManifest? SelectedManifest => manifest;
    public bool IsBusy => operations.Count > 0;
    public event EventHandler? BusyChanged;

    public MarketplacePage()
    {
        InitializeComponent();
        PublishButton.IsEnabled = RefreshButton.IsEnabled = false;
        PreviewKeyDown += Page_KeyDown;
        Unloaded += (_, _) => { Cancel(); imageLoading?.Cancel(); };
    }

    public MarketplacePage(MarketplacePageActions actions) : this() => Configure(actions);

    public void Configure(MarketplacePageActions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (active is not null) throw new InvalidOperationException("请先取消当前操作。");
        actions = value;
        PublishButton.IsEnabled = RefreshButton.IsEnabled = true;
        RefreshAccountState();
    }

    // MainWindow calls this only when the market tab is selected. Loaded never fetches.
    public Task EnsureLoadedAsync()
    {
        if (attemptedLoad) return PendingOperation;
        return PendingOperation = LoadAndRestoreIdentityAsync();
    }

    private async Task LoadAndRestoreIdentityAsync()
    {
        var revision = cancellationRevision;
        await RefreshAsync();
        if (actions?.RefreshUserAsync is null || revision != cancellationRevision) return;
        var catalogueStatus = StatusText.Text;
        const string message = "正在读取 GitHub 登录状态…";
        await AccountOperationAsync(async token =>
        {
            var restored = await actions.RefreshUserAsync(token);
            token.ThrowIfCancellationRequested();
            user = restored;
        }, message);
        if (StatusText.Text == message) StatusText.Text = catalogueStatus;
    }

    private void RefreshAccountState()
    {
        if (actions?.CurrentUser is not null) user = actions.CurrentUser();
        AuthorMenuButton.Content = user is null ? "未登录 ▾" : "GitHub · " + user.Login + " ▾";
        AuthorStatus.Text = user is null ? "登录后可提交和更新自己发布的排序。" : "当前账号：" + user.Login;
        LoginButton.IsEnabled = !IsBusy && user is null && actions?.LoginAsync is not null;
        LogoutButton.IsEnabled = !IsBusy && user is not null;
        MyOrdersButton.IsEnabled = !IsBusy && actions is not null;
    }

    private async Task AccountOperationAsync(Func<CancellationToken, Task> operation, string message)
    {
        if (IsBusy) return;
        var source = BeginOperation(message);
        try { await operation(source.Token); source.Token.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException) { StatusText.Text = "登录操作已取消。"; }
        catch (Exception ex) { StatusText.Text = "无法完成 GitHub 登录操作：" + Safe(ex.Message); }
        finally { EndOperation(source); RefreshAccountState(); }
    }

    private void AuthorMenu_Click(object sender, RoutedEventArgs e) => AuthorPopup.IsOpen = !AuthorPopup.IsOpen;
    private void Login_Click(object sender, RoutedEventArgs e)
    {
        if (actions?.LoginAsync is null) return;
        PendingOperation = AccountOperationAsync(async token =>
        {
            var authenticated = await actions.LoginAsync(token);
            token.ThrowIfCancellationRequested();
            user = authenticated;
            StatusText.Text = "已登录 GitHub · " + user.Login;
        }, "请在浏览器完成 GitHub 授权…");
    }
    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        try { actions?.Logout?.Invoke(); user = null; RefreshAccountState(); StatusText.Text = "已退出 GitHub 登录。"; }
        catch (Exception ex) { StatusText.Text = "无法退出登录：" + Safe(ex.Message); }
    }
    private void MyOrders_Click(object sender, RoutedEventArgs e)
    {
        if (actions is null || IsBusy) return;
        try
        {
            var entries = cards.Select(card => card.Entry).Where(entry => user is not null && entry.Author.Equals(user.Login, StringComparison.OrdinalIgnoreCase)).ToArray();
            var dialog = new MarketplaceMyOrdersWindow(entries, actions.LoadDrafts?.Invoke() ?? [], new MarketplaceMyOrdersActions
            { DeleteDraft = actions.DeleteDraft, DeleteOrderAsync = actions.DeleteOrderAsync }) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            if (dialog.SelectedDraft is { } draft) actions.OpenDraft?.Invoke(draft);
            else if (dialog.SelectedEntry is { } entry) actions.UpdateOrder?.Invoke(entry);
        }
        catch (Exception ex) { StatusText.Text = "无法打开我的排序与草稿：" + Safe(ex.Message); }
    }

    public Task RefreshAsync()
    {
        if (actions is null || downloading) return Task.CompletedTask;
        attemptedLoad = true;
        return PendingOperation = LoadCatalogueAsync();
    }

    public void Cancel()
    {
        cancellationRevision++;
        foreach (var operation in operations.ToArray()) operation.Cancel();
    }

    private CancellationTokenSource BeginOperation(string status)
    {
        active?.Cancel();
        var source = new CancellationTokenSource();
        active = source;
        operations.Add(source);
        BusyChanged?.Invoke(this, EventArgs.Empty);
        StatusText.Text = status;
        BusyProgress.Visibility = CancelButton.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        PublishButton.IsEnabled = actions is not null && !downloading;
        UpdateActions();
        RefreshAccountState();
        return source;
    }

    private bool Current(CancellationTokenSource source) => ReferenceEquals(active, source) && !source.IsCancellationRequested;

    private void EndOperation(CancellationTokenSource source)
    {
        operations.Remove(source);
        if (ReferenceEquals(active, source)) active = null;
        BusyChanged?.Invoke(this, EventArgs.Empty);
        if (!IsBusy)
        {
            downloading = false;
            ResultsList.IsEnabled = true;
            BusyProgress.Visibility = CancelButton.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = PublishButton.IsEnabled = actions is not null;
            UpdateActions();
            RefreshAccountState();
        }
        source.Dispose();
    }

    private async Task LoadCatalogueAsync()
    {
        var source = BeginOperation("正在读取市场…");
        try
        {
            var result = await actions!.LoadCatalogueAsync(source.Token);
            if (!Current(source)) return;
            (string Author, string Id, string Version, string Hash)? oldKey = selected is null ? null : EntryKey(selected.Entry);
            cards = result.Value.Entries.Select(entry => new MarketplaceCard(entry)).ToArray();
            catalogueFromCache = result.FromCache;
            if (!result.FromCache) manifests.Clear();
            CacheBanner.Visibility = result.FromCache || !string.IsNullOrWhiteSpace(result.Warning) ? Visibility.Visible : Visibility.Collapsed;
            CacheText.Text = result.FromCache ? "当前显示上次保存的市场缓存。" : "";
            if (!string.IsNullOrWhiteSpace(result.Warning)) CacheText.Text += " " + Safe(result.Warning);
            selected = oldKey is null ? null : cards.FirstOrDefault(card => EntryKey(card.Entry) == oldKey);
            selected ??= cards.FirstOrDefault();
            manifest = null;
            FilterResults();
            if (selected is not null) RenderEntry(selected.Entry);
            StatusText.Text = cards.Count == 0 ? "市场暂无已发布的排序。" : $"已读取 {cards.Count} 份排序";
            if (cards.Count == 0) HideDetails();
        }
        catch (OperationCanceledException) { if (ReferenceEquals(active, source)) StatusText.Text = "读取已取消，保留上次完整结果。"; }
        catch (Exception ex)
        {
            if (!Current(source)) return;
            StatusText.Text = "无法读取市场：" + Safe(ex.Message);
            if (cards.Count == 0)
            {
                EmptyTitle.Text = "暂时无法连接市场";
                EmptyDescription.Text = "请稍后刷新，或检查网络连接。你仍可填写发布草稿或导出本地发布包。";
                EmptyPanel.Visibility = Visibility.Visible;
            }
        }
        finally { EndOperation(source); }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (ResultsList is null) return;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterResults();
    }

    private void FilterResults()
    {
        var query = SearchBox.Text.Trim();
        var visible = cards.Where(card => query.Length == 0 || card.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        changingResults = true;
        try
        {
            ResultsList.ItemsSource = visible;
            ResultsList.SelectedItem = selected is not null && visible.Contains(selected) ? selected : null;
        }
        finally { changingResults = false; }
        CountText.Text = query.Length == 0 ? $"{cards.Count} 份排序" : $"{visible.Length} / {cards.Count} 份排序";
        EmptyPanel.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = cards.Count > 0 ? "没有匹配的排序" : attemptedLoad ? "市场暂无排序" : "尚未读取市场";
        EmptyDescription.Text = cards.Count > 0 ? "试试其他标题、作者或游戏版本。" : attemptedLoad ? "还没有可浏览的已发布排序，可稍后刷新。" : "进入市场后读取已发布的排序。";
    }

    private void Result_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (changingResults || ResultsList.SelectedItem is not MarketplaceCard card || ReferenceEquals(selected, card)) return;
        selected = card;
        manifest = null;
        RenderEntry(card.Entry);
        DetailScroll.ScrollToTop();
        InterfaceMotion.SlidePage(DetailContent, fromRight: true);
    }

    private void RenderEntry(MarketplaceEntry entry)
    {
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailTitle.Text = entry.Title;
        _ = LoadImagesAsync(entry);
        DetailAuthor.Text = "作者  " + entry.Author;
        DetailId.Text = "排序标识  " + entry.Id;
        DetailVersion.Text = "版本 " + entry.Version;
        DetailGameVersion.Text = "RimWorld " + entry.GameVersion;
        DetailModCount.Text = entry.ModCount is { } count ? $"{count} 个模组" : "下载后读取模组数";
        DetailDescription.Text = string.IsNullOrWhiteSpace(entry.Description) ? "作者未填写简介。" : entry.Description;
        DetailReleaseNotes.Text = string.IsNullOrWhiteSpace(entry.ReleaseNotes) ? "作者未填写更新日志。" : entry.ReleaseNotes;
        DetailDlc.Text = entry.RequiredDlc.Count == 0 ? "未声明额外 DLC" : string.Join("、", entry.RequiredDlc.Select(GameContentNames.DlcName));
        WorkshopPanel.Visibility = string.IsNullOrWhiteSpace(entry.WorkshopCollectionId) ? Visibility.Collapsed : Visibility.Visible;
        WorkshopText.Text = entry.WorkshopCollectionId is { Length: > 0 } id ? "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id : "";
        DetailWarning.Text = catalogueFromCache ? "当前显示缓存目录，下载时将校验排序文件。" : "";
        UpdateActions();
    }

    private async Task LoadImagesAsync(MarketplaceEntry entry)
    {
        imageLoading?.Cancel();
        var source = imageLoading = new CancellationTokenSource();
        DetailImages.Children.Clear();
        try
        {
            using var client = new MarketplaceClient(System.IO.Path.Combine(UserStorage.DirectoryPath, "Cache", "MarketplaceImages"));
            foreach (var image in entry.Images)
            {
                var bytes = await client.GetImageAsync(image, source.Token);
                var bitmap = await Task.Run(() => MarketplaceImagePreview.Decode(bytes), source.Token);
                if (source.IsCancellationRequested || !ReferenceEquals(imageLoading, source)) return;
                DetailImages.Children.Add(new Image { Source = bitmap, MaxHeight = 280, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 0, 0, 10) });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (ReferenceEquals(imageLoading, source)) DetailImages.Children.Add(new TextBlock { Text = "图片暂时无法读取，不影响排序下载与使用。", TextWrapping = TextWrapping.Wrap }); }
        finally { if (ReferenceEquals(imageLoading, source)) imageLoading = null; source.Dispose(); }
    }

    private void HideDetails()
    {
        manifest = null;
        DetailPlaceholder.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
        UpdateActions();
    }

    private void UpdateActions()
    {
        if (DownloadButton is null) return;
        DownloadButton.IsEnabled = UseButton.IsEnabled = selected is not null && !IsBusy && actions is not null;
    }

    private async Task DownloadAsync(bool use)
    {
        if (selected is not { } card || active is not null || actions is null) return;
        downloading = true;
        ResultsList.IsEnabled = false;
        var source = BeginOperation(use ? "正在下载并准备使用排序…" : "正在下载排序…");
        void Notify(string title, string message, bool busy, double? percent = null) =>
            actions.NotifyDownload?.Invoke(new(title, message, busy, percent));
        var title = use ? "正在下载排序并使用" : "正在下载排序并另存为";
        var transferring = true;
        Notify(title, "正在下载排序文件…", true);
        var progress = new Progress<string>(message =>
        {
            if (!Current(source)) return;
            StatusText.Text = Safe(message);
            Notify(title, StatusText.Text, true);
        });
        var transfer = new Progress<MarketplaceTransferProgress>(value =>
        {
            if (!transferring || !Current(source)) return;
            var percent = value.Total is > 0 ? Math.Clamp(value.Received * 100d / value.Total.Value, 0, 100) : (double?)null;
            var message = percent is { } amount ? $"正在下载排序文件：{amount:0}%（{value.Received:N0} / {value.Total:N0} 字节）"
                : $"正在下载排序文件：已接收 {value.Received:N0} 字节";
            Notify(title, message, true, percent);
        });
        try
        {
            if (!manifests.TryGetValue(EntryKey(card.Entry), out var result))
                result = actions.LoadManifestWithProgressAsync is { } load
                    ? await load(card.Entry, transfer, source.Token)
                    : await actions.LoadManifestAsync(card.Entry, source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (!Current(source)) return;
            transferring = false;
            if (manifests.Count >= 16) manifests.Remove(manifests.Keys.First());
            manifests[EntryKey(card.Entry)] = result;
            var snapshot = manifest = result.Value;
            DetailModCount.Text = $"{snapshot.Mods.Count} 个模组";
            DetailWarning.Text = result.FromCache ? "使用已通过目录哈希校验的本地排序缓存。" : "";
            if (!string.IsNullOrWhiteSpace(result.Warning)) DetailWarning.Text += " " + Safe(result.Warning);
            Notify(title, use ? "排序文件已校验，正在准备应用…" : "排序文件已校验，请选择另存位置…", true);
            var outcome = await actions.DownloadAsync(snapshot, use, progress, source.Token);
            if (ReferenceEquals(active, source))
            {
                StatusText.Text = Safe(outcome.Message);
                Notify(outcome.Success ? use ? "已成功使用排序" : "排序已另存为" : "排序操作未完成",
                    StatusText.Text, false, outcome.Success ? 100 : null);
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(active, source)) { StatusText.Text = use ? "已取消等待，保留原排序；Steam 已接收的下载可能仍会继续。" : "已取消排序操作。"; Notify("排序操作已取消", StatusText.Text, false); }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(active, source)) { StatusText.Text = "排序操作未完成：" + Safe(ex.Message); Notify("排序操作未完成", StatusText.Text, false); }
        }
        finally { EndOperation(source); }
    }

    private void Workshop_Click(object sender, RoutedEventArgs e)
    {
        var id = selected?.Entry.WorkshopCollectionId;
        if (actions is null || string.IsNullOrEmpty(id) || !id.All(char.IsAsciiDigit)) return;
        try { actions.OpenTrustedLink(new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=" + id)); }
        catch (Exception ex) { StatusText.Text = "无法打开工坊合集：" + Safe(ex.Message); }
    }

    private void Publish_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            actions?.PublishCurrent();
            if (actions?.RefreshUserAsync is not null)
                PendingOperation = AccountOperationAsync(async token => user = await actions.RefreshUserAsync(token), "正在同步 GitHub 登录状态…");
        }
        catch (Exception ex) { StatusText.Text = "无法打开发布窗口：" + Safe(ex.Message); }
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();
    private void Download_Click(object sender, RoutedEventArgs e) => PendingOperation = DownloadAsync(false);
    private void Use_Click(object sender, RoutedEventArgs e) => PendingOperation = DownloadAsync(true);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();
    private void Page_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && RefreshButton.IsEnabled) { e.Handled = true; _ = RefreshAsync(); }
        else if (e.Key == Key.Escape && active is not null) { e.Handled = true; Cancel(); }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; SearchBox.Focus(); SearchBox.SelectAll(); }
    }
    private static (string Author, string Id, string Version, string Hash) EntryKey(MarketplaceEntry entry) => (entry.Author, entry.Id, entry.Version, entry.Sha256);
    private static string Safe(string value) => value.Any(c => c is >= '\u3400' and <= '\u9fff')
        ? new DiagnosticRedactor().Text(value) : "市场数据请求或校验未完成，请稍后重试。";

    private sealed record MarketplaceCard(MarketplaceEntry Entry)
    {
        public string Monogram => string.IsNullOrWhiteSpace(Entry.Title) ? "R" : System.Globalization.StringInfo.GetNextTextElement(Entry.Title.Trim());
        public string Summary => Entry.Description.Replace('\r', ' ').Replace('\n', ' ');
        public string VersionLabel => $"RimWorld {Entry.GameVersion}  ·  v{Entry.Version}";
        public string SearchText => string.Join('\n', Entry.Title, Entry.Author, Entry.Description, Entry.GameVersion, Entry.Version);
    }
}
