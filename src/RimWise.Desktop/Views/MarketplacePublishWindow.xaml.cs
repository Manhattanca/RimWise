using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Services;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Marketplace;
using RimWise.Integration.Steam;

namespace RimWise.Desktop.Views;

public sealed class MarketplacePublishActions
{
    public string ClientId { get; init; } = "";
    public bool ClientSecretConfigured { get; set; }
    public bool IsUpdate { get; init; }
    public GitHubMarketplaceUser? InitialUser { get; init; }
    public required Func<string, Action<Uri>, CancellationToken, Task<GitHubMarketplaceUser>> LoginAsync { get; init; }
    public required Func<MarketplaceManifest, IProgress<string>, CancellationToken, Task<MarketplaceSubmission>> SubmitAsync { get; init; }
    public required Action Logout { get; init; }
    public required Action<Uri> OpenTrustedLink { get; init; }
    public Func<MarketplaceManifest, IReadOnlyList<MarketplaceImageUpload>, IProgress<string>, CancellationToken, Task<MarketplaceSubmission>>? SubmitImagesAsync { get; init; }
    public Action<MarketplaceManifest, IReadOnlyList<byte[]>>? SaveImageDraft { get; init; }
    public IReadOnlyList<byte[]> InitialImages { get; init; } = [];
    public Action<MarketplaceManifest>? SaveDraft { get; init; }
    public Func<MarketplaceManifest?>? LoadDraft { get; init; }
    public Func<MarketplaceManifest, CancellationToken, Task<MarketplaceManifest>>? PrepareDraftAsync { get; init; }
    public Func<string, CancellationToken, Task<string>>? FetchCollectionDescriptionAsync { get; init; }
    public Func<CancellationToken, Task<IReadOnlyList<string>>>? LoadMarketplaceIdsAsync { get; init; }
}

public partial class MarketplacePublishWindow : Window
{
    private readonly MarketplaceManifest snapshot;
    private readonly List<byte[]> imageContents = [];
    private bool imagesReady;
    private readonly MarketplacePublishActions actions;
    private readonly DispatcherTimer collectionLookupTimer;
    private GitHubMarketplaceUser? user;
    private MarketplaceSubmission? submission;
    private string clientId;
    private string draftId;
    private CancellationTokenSource? active;
    private CancellationTokenSource? collectionLookup;
    private bool initialized;
    private bool changingVersionSelection;
    private bool applyingSteamDescription;
    private bool descriptionManuallyEdited;
    private bool closed;
    private bool closeWhenIdle;
    public bool IsBusy => active is not null;
    public event EventHandler? BusyChanged;
    public Task PendingOperation { get; private set; } = Task.CompletedTask;

    public MarketplacePublishWindow(MarketplaceManifest draft)
        : this(draft, CreateProductionActions(draft, UserStorage.DirectoryPath, OpenProductionLink)) { }

    public MarketplacePublishWindow(MarketplaceManifest draft,
        Func<MarketplaceManifest, CancellationToken, Task<MarketplaceManifest>> prepareDraftAsync,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? loadMarketplaceIdsAsync = null)
        : this(draft, CreateProductionActions(draft, UserStorage.DirectoryPath, OpenProductionLink,
            prepareDraftAsync, loadMarketplaceIdsAsync)) { }

    public MarketplacePublishWindow(MarketplaceManifest draft, MarketplaceAuthorSession authorSession,
        Func<MarketplaceManifest, CancellationToken, Task<MarketplaceManifest>> prepareDraftAsync,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? loadMarketplaceIdsAsync = null, bool isUpdate = false, MarketplaceSavedDraft? savedDraft = null)
        : this(draft, CreateProductionActions(draft, UserStorage.DirectoryPath, OpenProductionLink,
            prepareDraftAsync, loadMarketplaceIdsAsync, authorSession, isUpdate, savedDraft)) { }

    public MarketplacePublishWindow(MarketplaceManifest draft, MarketplacePublishActions actions)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(actions);
        // A dialog always edits its captured order, even if the main page changes later.
        snapshot = draft with { Mods = Array.AsReadOnly(draft.Mods.ToArray()), RequiredDlc = Array.AsReadOnly(draft.RequiredDlc.ToArray()) };
        this.actions = actions;
        clientId = actions.ClientId.Trim();
        user = actions.InitialUser;
        InitializeComponent();
        collectionLookupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        collectionLookupTimer.Tick += async (_, _) =>
        {
            collectionLookupTimer.Stop();
            await FetchCollectionDescriptionAsync();
        };
        var restored = false;
        string? restoreWarning = null;
        try
        {
            var saved = actions.LoadDraft?.Invoke();
            if (saved is not null && saved.Mods is not null && saved.RequiredDlc is not null
                && saved.Mods.SequenceEqual(snapshot.Mods)
                && saved.RequiredDlc.SequenceEqual(snapshot.RequiredDlc, StringComparer.OrdinalIgnoreCase)
                && saved.GameVersion == snapshot.GameVersion
                && (!actions.IsUpdate || saved.Id == snapshot.Id && saved.Author.Equals(snapshot.Author, StringComparison.OrdinalIgnoreCase)))
            {
                draft = saved;
                restored = true;
            }
        }
        catch (Exception ex) { restoreWarning = "无法恢复本地草稿：" + Safe(ex.Message); }
        TitleBox.Text = draft.Title;
        draftId = draft.Id;
        PublishedIdPanel.Visibility = actions.IsUpdate ? Visibility.Visible : Visibility.Collapsed;
        PublishedIdText.Text = actions.IsUpdate ? draft.Id : "";
        VersionBox.Text = draft.Version;
        GameVersionBox.Text = draft.GameVersion;
        CollectionBox.Text = draft.WorkshopCollectionId ?? "";
        DescriptionBox.Text = draft.Description;
        ReleaseNotesBox.Text = draft.ReleaseNotes;
        ReleaseNotesPanel.Visibility = actions.IsUpdate ? Visibility.Visible : Visibility.Collapsed;
        descriptionManuallyEdited = !string.IsNullOrWhiteSpace(draft.Description);
        SyncVersionChoicesFromText();
        SnapshotSummary.Text = $"当前排序快照 · {draft.Mods.Count} 个模组";
        DlcSummary.Text = "所需 DLC：" + (draft.RequiredDlc.Count == 0 ? "未声明额外 DLC" : string.Join("、", draft.RequiredDlc.Select(GameContentNames.DlcName)));
        initialized = true;
        StatusText.Text = "填写排序信息后提交审核，也可先保存草稿。";
        DraftStatus.Text = restoreWarning ?? (restored ? "已恢复此排序的本地草稿" : "");
        imageContents.AddRange(actions.InitialImages.Select(bytes => bytes.ToArray()));
        imagesReady = draft.Images.Count == 0 || imageContents.Count > 0;
        RenderImages();
        if (!imagesReady)
        {
            var originalImages = draft.Images.ToArray();
            Loaded += async (_, _) => await RunAsync("正在读取已发布图片…", async token =>
            {
                using var client = new MarketplaceClient();
                var loaded = new List<byte[]>();
                foreach (var image in originalImages) loaded.Add(await client.GetImageAsync(image, token));
                token.ThrowIfCancellationRequested();
                imageContents.Clear(); imageContents.AddRange(loaded); imagesReady = true; RenderImages();
                StatusText.Text = "已恢复原排序图片，可修改后提交新版本。";
            });
        }
        RefreshState();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && IsBusy) { e.Handled = true; Cancel(); } };
    }

    public MarketplaceManifest Draft => ReadEditor(validateCollection: true);
    private MarketplaceManifest ReadEditor(bool validateCollection) => snapshot with
    {
        Images = [], Title = TitleBox.Text.Trim(), Id = draftId, Version = VersionBox.Text.Trim(),
        GameVersion = GameVersionBox.Text.Trim(), Description = DescriptionBox.Text,
        ReleaseNotes = actions.IsUpdate ? ReleaseNotesBox.Text.Trim() : "",
        Author = actions.IsUpdate && !validateCollection ? snapshot.Author : user?.Login ?? snapshot.Author,
        WorkshopCollectionId = !validateCollection ? CollectionBox.Text : string.IsNullOrWhiteSpace(CollectionBox.Text) ? null
            : ImportedOrderDownloads.WorkshopIdFromInput(CollectionBox.Text)
                ?? throw new InvalidDataException("请填写有效的 Steam 创意工坊合集编号或链接。")
    };

    public void Cancel() => active?.Cancel();

    public Task LoginAsync()
    {
        if (IsBusy || !HasOAuthConfiguration() || user is not null) return Task.CompletedTask;
        return PendingOperation = RunAsync("正在连接 GitHub…", async token =>
        {
            var loggedIn = await actions.LoginAsync(clientId, uri =>
            {
                token.ThrowIfCancellationRequested();
                OpenBrowserLink(uri);
            }, token);
            token.ThrowIfCancellationRequested();
            if (closed) return;
            user = loggedIn;
            StatusText.Text = "已登录，可提交排序审核。";
            MarkDraftChanged();
        });
    }

    public Task SubmitAsync()
    {
        if (IsBusy || user is null || submission is not null) return Task.CompletedTask;
        MarketplaceManifest draft;
        try { draft = Draft; }
        catch (Exception ex) { StatusText.Text = "请检查排序信息：" + Safe(ex.Message); return Task.CompletedTask; }
        return PendingOperation = RunAsync("正在提交排序审核…", async token =>
        {
            draft = await EnsureUniqueIdAsync(draft, token);
            draft = await PrepareDraftAsync(draft, token);
            var progress = new Progress<string>(message => { if (!closed && !token.IsCancellationRequested) StatusText.Text = Safe(message); });
            if (!imagesReady) throw new InvalidDataException("原图片尚未读取完成，请重新打开修改窗口后重试。");
            StatusText.Text = "正在自动压缩图片，优先保留画质…";
            var compressed = imageContents.Count == 0 ? []
                : await Task.Run(() => MarketplaceImageCompression.Compress(imageContents, token), token);
            token.ThrowIfCancellationRequested();
            var uploads = compressed.Select((bytes, i) => MarketplaceImages.Prepare(draft.Author, draft.Id, draft.Version, i, bytes)).ToArray();
            draft = draft with { Images = uploads.Select(item => item.Image).ToArray() };
            var result = actions.SubmitImagesAsync is { } submitImages
                ? await submitImages(draft, uploads, progress, token)
                : uploads.Length == 0 ? await actions.SubmitAsync(draft, progress, token)
                : throw new InvalidDataException("此投稿入口尚不支持图片。");
            // A completed mutation must be shown even if cancellation arrived after completion.
            if (closed) return;
            submission = result;
            SubmissionPanel.Visibility = Visibility.Visible;
            SubmissionLinkText.Text = "查看待审核提交 #" + result.PullRequestNumber;
            StatusText.Text = "提交已创建，等待审核；审核上架后请刷新市场。";
            MarkDraftChanged();
        });
    }

    public Task SaveLocalDraftAsync()
    {
        if (!IsBusy) SaveDraftNow();
        return Task.CompletedTask;
    }

    private async Task<MarketplaceManifest> EnsureUniqueIdAsync(MarketplaceManifest draft, CancellationToken token)
    {
        if (actions.IsUpdate) return draft;
        if (actions.LoadMarketplaceIdsAsync is null) return draft;
        StatusText.Text = "正在核对发布信息…";
        var ids = await actions.LoadMarketplaceIdsAsync(token);
        token.ThrowIfCancellationRequested();
        if (ids.Contains(draft.Id, StringComparer.OrdinalIgnoreCase))
        {
            draft = draft with { Id = MarketplaceJson.GenerateUniqueId(ids) };
            draftId = draft.Id;
            MarkDraftChanged();
            StatusText.Text = "发布信息已自动调整并完成核对。";
        }
        else StatusText.Text = "发布信息已核对。";
        return draft;
    }

    private async Task<MarketplaceManifest> PrepareDraftAsync(MarketplaceManifest draft, CancellationToken token)
    {
        if (actions.IsUpdate && !draft.Author.Equals(snapshot.Author, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新排序时必须使用原作者的 GitHub 账号。");
        if (actions.PrepareDraftAsync is not null)
        {
            StatusText.Text = "正在核对排序中的工坊来源，无需手动刷新…";
            draft = await actions.PrepareDraftAsync(draft, token);
        }
        token.ThrowIfCancellationRequested();
        ValidateForSubmission(draft);
        return draft;
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> work)
    {
        var source = new CancellationTokenSource();
        active = source;
        BusyChanged?.Invoke(this, EventArgs.Empty);
        StatusText.Text = message;
        RefreshState();
        try { await work(source.Token); }
        catch (OperationCanceledException) { if (!closed) StatusText.Text = "操作已取消。若已发出提交，请在 GitHub 核对结果后再重试。"; }
        catch (Exception ex) { if (!closed) StatusText.Text = "操作未完成：" + Safe(ex.Message); }
        finally
        {
            active = null;
            source.Dispose();
            BusyChanged?.Invoke(this, EventArgs.Empty);
            if (!closed) RefreshState();
            if (closeWhenIdle && !closed) Close();
        }
    }

    private void RefreshState()
    {
        EditorPanel.IsEnabled = !IsBusy && submission is null;
        var hasOAuthConfiguration = HasOAuthConfiguration();
        LoginButton.IsEnabled = !IsBusy && user is null && hasOAuthConfiguration;
        LogoutButton.IsEnabled = !IsBusy && user is not null;
        SubmitButton.IsEnabled = !IsBusy && user is not null && submission is null;
        SaveDraftButton.IsEnabled = !IsBusy;
        BusyProgress.Visibility = CancelButton.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        AuthorStatus.Text = user is null ? "尚未登录" : "GitHub · " + user.Login;
        AuthorMenuButton.Content = user is null ? "未登录  ▾" : "GitHub · " + user.Login + "  ▾";
        LoginStatus.Text = !hasOAuthConfiguration ? "此版本未配置 GitHub 发布登录，可先保存本地草稿。"
            : user is null ? "使用 GitHub 登录后，以你的账号提交排序。" : "提交作者将使用当前 GitHub 账号。";
    }

    private void Draft_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ReferenceEquals(sender, DescriptionBox) && initialized && !applyingSteamDescription)
            descriptionManuallyEdited = true;
        MarkDraftChanged();
    }

    private void GameVersion_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!changingVersionSelection) SyncVersionChoicesFromText();
        MarkDraftChanged();
    }

    private void GameVersionChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox changed || changed.Tag is not string selected) return;
        try
        {
            var current = MarketplaceJson.ParseGameVersions(GameVersionBox.Text);
            var options = GameVersionChoices.Children.OfType<System.Windows.Controls.CheckBox>()
                .Select(box => box.Tag as string).Where(value => value is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var values = new List<string>();
            foreach (var box in GameVersionChoices.Children.OfType<System.Windows.Controls.CheckBox>())
            {
                var series = (string)box.Tag;
                if (box.IsChecked != true) continue;
                var exact = current.FirstOrDefault(value => GameVersion.Parse(value)?.Series == series);
                values.Add(exact ?? series);
            }
            values.AddRange(current.Where(value => GameVersion.Parse(value)?.Series is { } series && !options.Contains(series)));
            changingVersionSelection = true;
            GameVersionBox.Text = string.Join(", ", values.DistinctBy(value => GameVersion.Parse(value)?.Series, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is MarketplaceException or InvalidDataException)
        { StatusText.Text = "游戏版本列表暂时无法同步，请检查手动填写的版本。"; }
        finally { changingVersionSelection = false; }
        MarkDraftChanged();
    }

    private void SyncVersionChoicesFromText()
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var value in MarketplaceJson.ParseGameVersions(GameVersionBox.Text))
            if (GameVersion.Parse(value) is { } version) versions.Add(version.Series); }
        catch (MarketplaceException) { }
        changingVersionSelection = true;
        foreach (var box in GameVersionChoices.Children.OfType<System.Windows.Controls.CheckBox>())
            box.IsChecked = box.Tag is string series && versions.Contains(series);
        changingVersionSelection = false;
    }

    private void Collection_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        MarkDraftChanged();
        collectionLookup?.Cancel();
        collectionLookupTimer.Stop();
        if (!initialized) return;
        if (string.IsNullOrWhiteSpace(CollectionBox.Text)) { CollectionFetchStatus.Text = ""; return; }
        if (ImportedOrderDownloads.WorkshopIdFromInput(CollectionBox.Text) is null)
        {
            CollectionFetchStatus.Text = "输入有效的 Steam 合集链接或数字编号后，将尝试读取简介。";
            return;
        }
        CollectionFetchStatus.Text = "正在等待读取 Steam 合集简介…";
        collectionLookupTimer.Start();
    }

    private async Task FetchCollectionDescriptionAsync()
    {
        if (actions.FetchCollectionDescriptionAsync is null || closed
            || ImportedOrderDownloads.WorkshopIdFromInput(CollectionBox.Text) is not { } id) return;
        collectionLookup?.Dispose();
        var source = collectionLookup = new CancellationTokenSource();
        CollectionFetchStatus.Text = "正在读取 Steam 合集简介…";
        try
        {
            var description = await actions.FetchCollectionDescriptionAsync(id, source.Token);
            if (closed || source.IsCancellationRequested || ImportedOrderDownloads.WorkshopIdFromInput(CollectionBox.Text) != id) return;
            if (!descriptionManuallyEdited)
            {
                applyingSteamDescription = true;
                DescriptionBox.Text = description;
                applyingSteamDescription = false;
                CollectionFetchStatus.Text = string.IsNullOrWhiteSpace(description)
                    ? "Steam 合集没有简介；可直接填写，留空也能提交。"
                    : "已填入 Steam 合集简介，可继续编辑。后续自动读取不会覆盖手动修改。";
                MarkDraftChanged();
            }
            else CollectionFetchStatus.Text = "已读取 Steam 合集；简介保留你的手动修改。";
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!closed && !source.IsCancellationRequested)
                CollectionFetchStatus.Text = "未能读取 Steam 合集简介；你仍可手动填写或留空提交。";
        }
    }

    private void AuthorMenu_Click(object sender, RoutedEventArgs e) => AuthorPopup.IsOpen = !AuthorPopup.IsOpen;
    private void MarkDraftChanged()
    {
        if (!initialized || closed) return;
        if (actions.SaveDraft is null && actions.SaveImageDraft is null) return;
        DraftStatus.Text = "修改尚未保存，点击“保存草稿”后才会保存";
    }

    public void SaveDraftNow()
    {
        if (!initialized || actions.SaveDraft is null && actions.SaveImageDraft is null) return;
        try { if (!imagesReady) throw new InvalidDataException("图片尚未读取完成，未覆盖草稿。");
            if (actions.SaveImageDraft is { } save) save(ReadEditor(validateCollection: false), imageContents);
            else actions.SaveDraft!(ReadEditor(validateCollection: false)); DraftStatus.Text = "草稿已保存到本机，可在“我的排序与草稿”中继续编辑"; }
        catch (Exception ex) { DraftStatus.Text = "草稿未能保存：" + Safe(ex.Message); }
    }

    public async Task AddImagesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy || !imagesReady) return;
        await RunAsync("正在无损处理图片…", async token =>
        {
            if (imageContents.Count + paths.Count > MarketplaceImages.MaximumCount) throw new InvalidDataException("每份排序最多三张图片。");
            var loaded = new List<byte[]>();
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length is <= 0 or > MarketplaceImages.MaximumBytes) throw new InvalidDataException("单张图片不能超过 5 MiB，请裁剪或更换图片。");
                var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token);
                var optimized = await Task.Run(() => MarketplaceImages.Optimize(bytes), token);
                _ = MarketplaceImagePreview.Decode(optimized, 220); loaded.Add(optimized);
            }
            token.ThrowIfCancellationRequested(); imageContents.AddRange(loaded); RenderImages(); MarkDraftChanged();
            StatusText.Text = "图片已加入；上传时自动压缩，尽量将总量控制在 7 MB 内，优先保留画质。草稿仅点击保存时保存。";
        });
    }
    private async void AddImages_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择排序图片（最多三张）", Filter = "PNG / JPEG 图片|*.png;*.jpg;*.jpeg", Multiselect = true };
        if (picker.ShowDialog(this) == true) await AddImagesAsync(picker.FileNames);
    }
    private void RenderImages()
    {
        ImageList.Children.Clear();
        for (int i = 0; i < imageContents.Count; i++)
        {
            int index = i;
            var panel = new System.Windows.Controls.StackPanel { Width = 210, Margin = new Thickness(0, 0, 10, 8) };
            panel.Children.Add(new System.Windows.Controls.Image { Source = MarketplaceImagePreview.Decode(imageContents[i], 220), Height = 115, Stretch = System.Windows.Media.Stretch.Uniform });
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = i == 0 ? "封面" : "图片 " + (i + 1), Margin = new Thickness(0, 5, 0, 3) });
            var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var remove = new System.Windows.Controls.Button { Content = "移除" };
            remove.Click += (_, _) => { imageContents.RemoveAt(index); RenderImages(); MarkDraftChanged(); };
            buttons.Children.Add(remove);
            if (i > 0)
            {
                var cover = new System.Windows.Controls.Button { Content = "设为封面" };
                cover.Click += (_, _) => { var bytes = imageContents[index]; imageContents.RemoveAt(index); imageContents.Insert(0, bytes); RenderImages(); MarkDraftChanged(); };
                buttons.Children.Add(cover);
            }
            panel.Children.Add(buttons); ImageList.Children.Add(panel);
        }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        try { actions.Logout(); user = null; RefreshState(); StatusText.Text = "已退出登录，本地草稿仍然保留。"; }
        catch (Exception ex) { StatusText.Text = "未能移除本地登录：" + Safe(ex.Message); }
    }

    private void OpenBrowserLink(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || uri.Port != 443 || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/login/oauth/authorize")
            throw new InvalidOperationException("GitHub 授权地址无效，已阻止打开。");
        StatusText.Text = "已打开 GitHub，请在浏览器完成授权…";
        actions.OpenTrustedLink(uri);
    }

    private bool HasOAuthConfiguration() => clientId.Length > 0 && actions.ClientSecretConfigured;
    private void OpenSubmission_Click(object sender, RoutedEventArgs e)
    {
        if (submission is { } result && result.PullRequestUri.Scheme == Uri.UriSchemeHttps
            && result.PullRequestUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && result.PullRequestUri.AbsolutePath.StartsWith(MarketplaceRepository.PullRequestPath, StringComparison.OrdinalIgnoreCase)) OpenLink(result.PullRequestUri);
    }
    private void OpenLink(Uri uri)
    {
        try { actions.OpenTrustedLink(uri); }
        catch (Exception ex) { StatusText.Text = "无法打开页面：" + Safe(ex.Message); }
    }
    private void Login_Click(object sender, RoutedEventArgs e) => _ = LoginAsync();
    private void Submit_Click(object sender, RoutedEventArgs e) => _ = SubmitAsync();
    private void SaveDraft_Click(object sender, RoutedEventArgs e) => _ = SaveLocalDraftAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(CancelEventArgs e)
    {
        if (IsBusy) { e.Cancel = true; closeWhenIdle = true; Cancel(); StatusText.Text = "正在取消操作并关闭…"; }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        collectionLookupTimer.Stop();
        collectionLookup?.Cancel();
        Cancel();
        base.OnClosed(e);
    }

    private static void ValidateForSubmission(MarketplaceManifest draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title)) throw new InvalidDataException("请填写排序标题。");
        MarketplaceJson.ValidateManifest(draft);
    }
    private static string Safe(string text) => text.Any(c => c is >= '\u3400' and <= '\u9fff')
        ? new DiagnosticRedactor().Text(text) : "市场请求或数据校验未完成，请稍后重试。";

    internal static MarketplacePublishActions CreateProductionActions(MarketplaceManifest snapshot, string profileDirectory,
        Action<Uri> openTrustedLink,
        Func<MarketplaceManifest, CancellationToken, Task<MarketplaceManifest>>? prepareDraftAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? loadMarketplaceIdsAsync = null,
        MarketplaceAuthorSession? authorSession = null, bool isUpdate = false, MarketplaceSavedDraft? savedDraft = null)
    {
        var store = new MarketplaceDraftStore(profileDirectory);
        var restored = savedDraft ?? store.ReadAll().FirstOrDefault(item => item.IsUpdate == isUpdate
            && item.Manifest.Mods.SequenceEqual(snapshot.Mods)
            && item.Manifest.RequiredDlc.SequenceEqual(snapshot.RequiredDlc, StringComparer.OrdinalIgnoreCase)
            && item.Manifest.GameVersion == snapshot.GameVersion
            && (!isUpdate || item.Manifest.Id == snapshot.Id && item.Manifest.Author.Equals(snapshot.Author, StringComparison.OrdinalIgnoreCase)));
        var key = restored?.Key ?? Guid.NewGuid().ToString("N");
        authorSession ??= new MarketplaceAuthorSession(profileDirectory);
        return new MarketplacePublishActions
        {
            ClientId = authorSession.ClientId, ClientSecretConfigured = authorSession.IsConfigured,
            InitialUser = authorSession.User, IsUpdate = isUpdate,
            OpenTrustedLink = openTrustedLink,
            PrepareDraftAsync = prepareDraftAsync,
            LoadMarketplaceIdsAsync = loadMarketplaceIdsAsync,
            SaveDraft = draft => store.Save(key, draft, isUpdate),
            SaveImageDraft = (draft, images) => store.Save(key, draft, isUpdate, images),
            InitialImages = restored?.ImageContents ?? [],
            SubmitImagesAsync = authorSession.SubmitWithImagesAsync,
            LoadDraft = () => restored?.Manifest,
            FetchCollectionDescriptionAsync = WorkshopDependencyDownloads.GetCollectionDescriptionAsync,
            Logout = authorSession.Logout,
            LoginAsync = (_, openBrowser, token) => authorSession.LoginAsync(openBrowser, token),
            SubmitAsync = authorSession.SubmitAsync
        };
    }

    private static void OpenProductionLink(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Port != 443
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return;
        if (uri.AbsolutePath != "/login/oauth/authorize"
            && !uri.AbsolutePath.StartsWith(MarketplaceRepository.PullRequestPath, StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
