using System.Drawing;
using RimWise.Integration.Marketplace;

namespace RimWise.MarketplaceReview;

internal sealed class ReviewWindow : Form
{
    private readonly ReviewApi api;
    private readonly ReviewSettings settings;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly NotifyIcon tray;
    private readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly TextBox text = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
    private readonly FlowLayoutPanel images = new() { Dock = DockStyle.Fill, AutoScroll = false, WrapContents = false, Padding = new Padding(8) };
    private readonly Label contentTitle = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold) };
    private readonly Label imageCaption = new() { Dock = DockStyle.Fill, Text = "上传图片 · 未上传图片", TextAlign = ContentAlignment.MiddleLeft };
    private readonly TextBox metadata = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
    private string? confirmedOutcome;
    private readonly Label account = new() { AutoSize = true, Text = "尚未登录审核账号", Padding = new Padding(8, 10, 8, 0) };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label schedule = new() { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(145, 171, 175) };
    private readonly Button login = new ReviewButton { Text = "登录审核账号", AutoSize = true };
    private readonly Button logout = new ReviewButton { Text = "退出登录", AutoSize = true };
    private readonly Button refresh = new ReviewButton { Text = "立即检查", AutoSize = true };
    private readonly Button inspect = new ReviewButton { Text = "查看投稿", AutoSize = true };
    private readonly Button approve = new ReviewButton { Text = "通过并上架", AutoSize = true };
    private readonly Button reject = new ReviewButton { Text = "退回修改", AutoSize = true };
    private readonly Button cancelButton = new ReviewButton { Text = "取消操作", AutoSize = true, Enabled = false };
    private readonly CheckBox startup = new() { Text = "开机自启动", AutoSize = true, Padding = new Padding(8, 8, 0, 0) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? active;
    private ReviewSnapshot? snapshot;
    private string? user;
    private bool exiting;
    private bool loadingList;
    private bool background;
    private readonly bool selfTest;

    internal ReviewWindow(bool background, bool selfTest = false, ReviewApi? testApi = null)
    {
        this.selfTest = selfTest;
        api = testApi ?? (selfTest ? new ReviewApi(new OfflineTransport(), new Uri("https://checks.invalid/")) : new ReviewApi());
        this.background = background;
        if (background) Opacity = 0;
        settings = selfTest ? new ReviewSettings() : ReviewSettings.Load();
        Text = "RimWise · 排序市场审核助手";
        Size = new Size(1160, 840); MinimumSize = new Size(880, 680); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 29, 33); ForeColor = Color.FromArgb(226, 235, 236); Font = new Font("Microsoft YaHei UI", 10);
        DoubleBuffered = true; Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        schedule.Text = $"排序市场服务器 · 每 {ReviewSettings.PollInterval.TotalMinutes:0} 分钟检查 · 空闲时暂停请求";

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 85)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = false };
        toolbar.Controls.AddRange([login, logout, refresh, startup, account]); layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(schedule, 0, 1);
        var split = new SplitContainer { Size = new Size(1000, 500), Dock = DockStyle.Fill, SplitterDistance = 330, FixedPanel = FixedPanel.Panel1, BackColor = Color.FromArgb(43, 60, 65) };
        split.Panel1.Controls.Add(list); split.Panel1.Padding = new Padding(1); split.Panel2.Padding = new Padding(1);
        var views = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        views.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); views.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var switcher = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var showContent = new ReviewButton { Text = "内容审核", AutoSize = true };
        var showMetadata = new ReviewButton { Text = "其他信息", AutoSize = true };
        switcher.Controls.AddRange([showContent, showMetadata]); views.Controls.Add(switcher, 0, 0);
        var contentTab = new Panel { Dock = DockStyle.Fill };
        var metadataTab = new Panel { Dock = DockStyle.Fill, Visible = false };
        var body = new Panel { Dock = DockStyle.Fill }; body.Controls.Add(metadataTab); body.Controls.Add(contentTab); views.Controls.Add(body, 0, 1);
        showContent.Click += (_, _) => { metadataTab.Visible = false; contentTab.Visible = true; contentTab.BringToFront(); };
        showMetadata.Click += (_, _) => { contentTab.Visible = false; metadataTab.Visible = true; metadataTab.BringToFront(); };
        var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(14) };
        detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        detail.Controls.Add(contentTitle, 0, 0); detail.Controls.Add(text, 0, 1);
        detail.Controls.Add(imageCaption, 0, 2); detail.Controls.Add(images, 0, 3);
        contentTab.Controls.Add(detail); metadataTab.Padding = new Padding(14); metadataTab.Controls.Add(metadata);
        split.Panel2.Controls.Add(views); layout.Controls.Add(split, 0, 2);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.Controls.Add(status, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        actions.Controls.AddRange([cancelButton, approve, reject, inspect]); footer.Controls.Add(actions, 0, 1); layout.Controls.Add(footer, 0, 3); Controls.Add(layout);
        ApplyTheme(this); approve.BackColor = Color.FromArgb(66, 174, 161); approve.ForeColor = Color.FromArgb(8, 29, 29);

        var menu = new ContextMenuStrip(); menu.Items.Add("打开审核助手", null, (_, _) => Reveal());
        menu.Items.Add("立即检查", null, async (_, _) => await RunAsync(CheckAsync));
        menu.Items.Add("退出", null, (_, _) => { exiting = true; Close(); });
        tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "RimWise 排序市场审核助手", ContextMenuStrip = menu, Visible = !selfTest };
        tray.DoubleClick += (_, _) => Reveal(); tray.BalloonTipClicked += (_, _) => Reveal();
        startup.Checked = !selfTest && ReviewSettings.StartupEnabled();
        startup.CheckedChanged += (_, _) => { if (selfTest) return; try { ReviewSettings.SetStartup(startup.Checked); status.Text = startup.Checked ? "已启用开机自启动。" : "已关闭开机自启动。"; } catch (Exception) { status.Text = "自启动设置未能修改，请检查本地权限。"; } };
        login.Click += async (_, _) => await RunAsync(LoginAsync);
        logout.Click += async (_, _) => await RunAsync(async token =>
        {
            try { await api.LogoutAsync(token); }
            finally
            {
                SetAccount(null);
                ReviewCredentials.Delete(api.Origin, settings.Username); settings.Username = ""; SaveSettings();
            }
            status.Text = "已退出登录，自动检查暂停。";
        });
        refresh.Click += async (_, _) => await RunAsync(CheckAsync);
        inspect.Click += async (_, _) => await RunAsync(InspectAsync);
        approve.Click += async (_, _) => await RunAsync(async token =>
        {
            if (snapshot is null) return;
            var current = snapshot;
            status.Text = "正在提交服务器审核结果…";
            await api.DecideAsync(current, approve: true, "", token);
            await FinishDecisionAsync(current, true, token);
        });
        reject.Click += async (_, _) =>
        {
            if (snapshot is null || active is not null) return;
            using var prompt = new ReviewReasonDialog();
            if (prompt.ShowDialog(this) != DialogResult.OK || prompt.Reason.Length == 0) return;
            var current = snapshot;
            await RunAsync(async token =>
            {
                status.Text = "正在退回投稿…";
                await api.DecideAsync(current, approve: false, prompt.Reason, token);
                await FinishDecisionAsync(current, false, token);
            });
        };
        list.DoubleClick += async (_, _) => await RunAsync(InspectAsync);
        list.SelectedIndexChanged += (_, _) => { if (loadingList) return; snapshot = null; ClearImages(); text.Clear(); metadata.Clear(); contentTitle.Text = ""; SetControls(); };
        cancelButton.Click += (_, _) => active?.Cancel();
        timer.Tick += async (_, _) =>
        {
            if (active is null && user is not null && DateTimeOffset.UtcNow >= settings.NextCheckUtc) await RunAsync(CheckAsync);
            else if (active is null && user is not null)
                schedule.Text = $"排序市场服务器 · 下次检查 {settings.NextCheckUtc.ToLocalTime():HH:mm:ss} · 当前暂停 API 请求";
        };
        Shown += async (_, _) =>
        {
            if (selfTest) return;
            if (this.background) { Hide(); Opacity = 1; this.background = false; }
            await RunAsync(async token =>
            {
                SetAccount(await api.RestoreAsync(token));
                if (user is null && settings.ServerOrigin == api.Origin.AbsoluteUri && settings.Username.Length > 0)
                {
                    var saved = ReviewCredentials.Read(api.Origin, settings.Username);
                    if (saved is not null) SetAccount(await api.LoginAsync(settings.Username, saved, token));
                }
                if (user is null) { status.Text = "请登录服务器上的排序市场审核员账号。"; return; }
                if (DateTimeOffset.UtcNow >= settings.NextCheckUtc) await CheckAsync(token);
            });
            timer.Start();
        };
        FormClosing += (_, e) =>
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            lifetime.Cancel(); active?.Cancel(); timer.Stop(); tray.Visible = false;
        };
        images.SizeChanged += (_, _) => LayoutImagePreviews();
        SetControls();
        text.Text = "登录排序市场审核员账号并检查投稿后，双击左侧条目查看简介、更新日志和图片。\r\n\r\n内容以纯文本及图片显示，不执行投稿内容。\r\n\r\n确认合规后可通过并上架，或填写原因退回修改。审核结果直接保存到排序市场服务器。";
    }

    private static void ApplyTheme(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            control.ForeColor = Color.FromArgb(226, 235, 236); control.BackColor = Color.FromArgb(25, 38, 43);
            if (control is Button button) { button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = Color.FromArgb(65, 94, 98); button.Padding = new Padding(9, 6, 9, 6); button.Margin = new Padding(0, 0, 8, 6); }
            ApplyTheme(control);
        }
    }

    private void SetControls()
    {
        bool idle = active is null;
        login.Enabled = idle && user is null; logout.Enabled = refresh.Enabled = idle && user is not null;
        list.Enabled = idle; inspect.Enabled = idle && user is not null && list.SelectedItem is MarketReviewItem;
        bool canDecide = idle && user is not null && snapshot is { Supported: true };
        approve.Enabled = canDecide; reject.Enabled = idle && user is not null && snapshot is not null; cancelButton.Enabled = !idle;
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (active is not null || exiting) return;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); active = source; SetControls();
        confirmedOutcome = null;
        try { await work(source.Token); }
        catch (ReviewAccountException ex)
        {
            SetAccount(null);
            if (!exiting) status.Text = confirmedOutcome is null ? ex.Message : confirmedOutcome + ex.Message + "无需重复提交。";
        }
        catch (OperationCanceledException) { if (!exiting) status.Text = confirmedOutcome is null ? "操作已取消；请刷新核对服务器上的审核状态。" : confirmedOutcome + "列表刷新已取消，无需重复提交。"; }
        catch (Exception ex) { if (!exiting) status.Text = confirmedOutcome is not null ? confirmedOutcome + "后续界面刷新未完成，请点击立即检查，无需重复提交。" : ex is InvalidOperationException or InvalidDataException or MarketplaceException
            && ex.Message.Any(c => c is >= '\u3400' and <= '\u9fff') ? ex.Message : "操作未完成，请检查服务器连接或审核账号后重试。"; }
        finally { active = null; if (!exiting) SetControls(); }
    }

    private async Task LoginAsync(CancellationToken token)
    {
        using var prompt = new ReviewLoginDialog(settings.Username);
        if (prompt.ShowDialog(this) != DialogResult.OK) return;
        if (prompt.Username.Length == 0 || prompt.Password.Length == 0) throw new InvalidDataException("请输入审核账号和密码。");
        status.Text = "正在连接排序市场服务器并验证审核权限…";
        var display = await api.LoginAsync(prompt.Username, prompt.Password, token);
        token.ThrowIfCancellationRequested();
        SetAccount(display);
        settings.Username = prompt.Username; settings.ServerOrigin = api.Origin.AbsoluteUri; SaveSettings();
        try { ReviewCredentials.Write(api.Origin, prompt.Username, prompt.Password); }
        catch (System.ComponentModel.Win32Exception)
        { status.Text = "本次登录有效，但无法保存本机登录记录；重启后需要重新登录。"; }
        await CheckAsync(token);
    }

    private async Task CheckAsync(CancellationToken token)
    {
        if (user is null) throw new InvalidOperationException("请先登录排序市场审核账号。");
        settings.LastCheckUtc = DateTimeOffset.UtcNow; SaveSettings(); status.Text = "正在检查服务器待审核排序…";
        // The label is only a display cache, never proof of current authority.
        var display = await api.RestoreAsync(token);
        token.ThrowIfCancellationRequested();
        if (display is null) throw new ReviewAccountException();
        SetAccount(display);
        var items = await api.ListAsync(token); token.ThrowIfCancellationRequested();
        var changed = settings.Notifications.Update(items); SaveSettings();
        string? selected = (list.SelectedItem as MarketReviewItem)?.Id;
        loadingList = true;
        try { list.Items.Clear(); foreach (var item in items) list.Items.Add(item); if (selected is not null) list.SelectedItem = items.FirstOrDefault(item => item.Id == selected); }
        finally { loadingList = false; }
        if (snapshot is not null && !items.Any(item => item.Id == snapshot.Item.Id && item.ReviewToken == snapshot.Item.ReviewToken))
        { snapshot = null; ClearImages(); text.Text = "所查看的投稿已处理或内容已变化，请重新选择。"; }
        if (changed.Count > 0) Notify("有新的排序投稿待审核", $"{changed.Count} 份投稿新增或更新，点击打开审核助手。");
        status.Text = $"{DateTimeOffset.Now:HH:mm} 检查完成 · 待审 {items.Count} 项 · {ReviewSettings.PollInterval.TotalMinutes:0} 分钟后再次检查";
    }

    private async Task InspectAsync(CancellationToken token)
    {
        if (list.SelectedItem is not MarketReviewItem item) return;
        snapshot = null; ClearImages(); status.Text = "正在读取投稿文字和图片…";
        var loaded = await api.InspectAsync(item, token); token.ThrowIfCancellationRequested(); RenderSnapshot(loaded);
        status.Text = loaded.Warning;
    }

    internal void PreviewForChecks(ReviewSnapshot value)
    {
        list.Items.Clear(); list.Items.Add(value.Item); list.SelectedItem = value.Item; RenderSnapshot(value); SetControls();
    }

    private void RenderSnapshot(ReviewSnapshot loaded)
    {
        try
        {
            foreach (var image in loaded.Images)
            {
                using var input = new MemoryStream(image.Content, false); using var original = Image.FromStream(input, true, true);
                var copy = new Bitmap(original);
                var panel = new PictureBox { Width = 250, Height = 205, SizeMode = PictureBoxSizeMode.Zoom, Image = copy, Margin = new Padding(0, 0, 10, 0) };
                panel.DoubleClick += (_, _) =>
                {
                    using var viewer = new Form { Text = "投稿图片 · 双击关闭", Width = 950, Height = 750, BackColor = BackColor, StartPosition = FormStartPosition.CenterParent };
                    var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = copy };
                    picture.DoubleClick += (_, _) => viewer.Close(); viewer.Controls.Add(picture); viewer.ShowDialog(this);
                };
                images.Controls.Add(panel);
            }
            contentTitle.Text = loaded.Item.Title;
            text.Text = "排序简介：\r\n" + (string.IsNullOrWhiteSpace(loaded.Item.Description) ? "未填写简介（允许提交）" : loaded.Item.Description)
                + "\r\n\r\n更新日志：\r\n" + (string.IsNullOrWhiteSpace(loaded.Item.ReleaseNotes) ? "未填写更新日志" : loaded.Item.ReleaseNotes);
            LayoutImagePreviews();
            metadata.Text = loaded.Text;
            imageCaption.Text = loaded.Images.Count == 0 ? "上传图片 · 未上传图片（允许提交）" : $"上传图片 · {loaded.Images.Count} 张 · 双击图片放大查看";
            snapshot = loaded;
        }
        catch { ClearImages(); snapshot = loaded with { Supported = false, Warning = "图片无法完整解码，已禁用通过。" }; text.Text = loaded.Text; }
        SetControls();
    }

    private void LayoutImagePreviews()
    {
        if (images.Controls.Count == 0) return;
        int width = Math.Max(80, (images.ClientSize.Width - images.Padding.Horizontal) / images.Controls.Count - 10);
        int height = Math.Max(60, images.ClientSize.Height - images.Padding.Vertical);
        foreach (Control picture in images.Controls) picture.Size = new Size(width, height);
    }

    private void ClearImages()
    {
        images.SuspendLayout();
        try
        {
            foreach (var child in images.Controls.Cast<Control>().ToArray())
            {
                // Detach before disposal: PictureBox.Image can synchronously repaint on layout changes.
                var bitmap = (child as PictureBox)?.Image;
                if (child is PictureBox picture) picture.Image = null;
                images.Controls.Remove(child); child.Dispose(); bitmap?.Dispose();
            }
        }
        finally { images.ResumeLayout(); }
    }

    private async Task FinishDecisionAsync(ReviewSnapshot current, bool approved, CancellationToken token)
    {
        confirmedOutcome = approved ? "服务器已确认通过并上架。" : "服务器已确认退回投稿，原因已发送给作者。";
        snapshot = null; status.Text = text.Text = confirmedOutcome;
        imageCaption.Text = "上传图片 · 投稿已处理";
        contentTitle.Text = current.Item.Title;
        try
        {
            ClearImages(); metadata.Clear();
            loadingList = true;
            try
            {
                foreach (var item in list.Items.Cast<MarketReviewItem>().Where(item => item.Id == current.Item.Id).ToArray())
                    list.Items.Remove(item);
            }
            finally { loadingList = false; }
            Notify(approved ? "排序已通过审核" : "投稿已退回", current.Item.Title + (approved ? " 已上架。" : " 已退回修改。"));
            await CheckAsync(token);
            status.Text = confirmedOutcome;
        }
        catch (ReviewAccountException ex)
        { SetAccount(null); status.Text = confirmedOutcome + ex.Message + "无需重复提交。"; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { status.Text = confirmedOutcome + "后续界面刷新未完成，请点击立即检查，无需重复提交。"; }
    }

    private void Notify(string title, string message)
    {
        if (!tray.Visible) return;
        try { tray.ShowBalloonTip(7000, title, message, ToolTipIcon.Info); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { /* Notification availability must not change a committed server result. */ }
    }

    internal void HandleUnexpectedUiError(Exception error)
    {
        var message = confirmedOutcome is not null ? confirmedOutcome + "界面刷新发生错误，请点击立即检查，无需重复提交。"
            : "审核助手界面发生错误，请刷新核对服务器上的审核状态。";
        status.Text = message;
        // Store only type and stack frames, never submitted content, credentials or HTTP payloads.
        try
        {
            Directory.CreateDirectory(ReviewSettings.DirectoryPath);
            File.AppendAllText(Path.Combine(ReviewSettings.DirectoryPath, "ui-errors.log"),
                DateTimeOffset.UtcNow.ToString("O") + " " + error.GetType().FullName + "\r\n" + error.StackTrace + "\r\n");
        }
        catch (IOException) { }
        MessageBox.Show(this, message, "RimWise 审核助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private sealed class OfflineTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("离线界面测试不能联网。");
    }

    private void SaveSettings() { if (!selfTest) settings.Save(); }
    private void SetAccount(string? display)
    {
        bool wasLoggedIn = user is not null;
        user = display;
        account.Text = display is null ? "尚未登录审核账号" : "审核账号 · " + display;
        if (display is not null)
        {
            if (!wasLoggedIn) text.Text = "已登录审核账号。请双击左侧待审条目查看投稿内容。";
            return;
        }
        loadingList = true;
        try { list.Items.Clear(); } finally { loadingList = false; }
        snapshot = null; ClearImages(); metadata.Clear();
        contentTitle.Text = ""; text.Text = "请登录排序市场审核员账号后检查投稿。";
        imageCaption.Text = "上传图片 · 尚未读取投稿";
        schedule.Text = "排序市场服务器 · 尚未登录 · 已暂停自动检查";
    }
    internal string StatusForChecks => status.Text;
    internal string ContentTextForChecks => text.Text;
    internal bool ContentVisibleForChecks => contentTitle.Text.Length > 0 && text.Text.Length > 0 && imageCaption.Text.Contains("上传图片");
    internal bool LoggedInForChecks => user is not null;
    internal bool LoginEnabledForChecks => login.Enabled;
    internal bool ReviewEnabledForChecks => approve.Enabled || reject.Enabled || inspect.Enabled;
    internal int ItemsForChecks => list.Items.Count;
    internal string AccountForChecks => account.Text;
    internal string ScheduleForChecks => schedule.Text;
    internal Task CheckForChecksAsync() => RunAsync(CheckAsync);
    internal Task InspectForChecksAsync() => RunAsync(InspectAsync);
    internal Task LoginForChecksAsync() => RunAsync(async token =>
    {
        SetAccount(await api.LoginAsync("reviewer", "synthetic-password", token));
        await CheckAsync(token);
    });
    internal async Task DecideForChecksAsync(ReviewSnapshot value, bool approved)
    {
        user = "测试审核员";
        await RunAsync(async token =>
        {
            await api.DecideAsync(value, approved, approved ? "" : "请修改", token);
            await FinishDecisionAsync(value, approved, token);
        });
    }

    private void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    protected override void OnHandleCreated(EventArgs e)
    { base.OnHandleCreated(e); int dark = 1; _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); tray.Dispose(); ClearImages(); api.Dispose(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
