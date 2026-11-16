using System.Diagnostics;
using System.Drawing;
using RimWise.Integration.Marketplace;

namespace RimWise.MarketplaceReview;

internal sealed class ReviewWindow : Form
{
    private readonly ReviewApi api = new();
    private readonly ReviewSettings settings;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly NotifyIcon tray;
    private readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly TextBox text = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
    private readonly FlowLayoutPanel images = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, Padding = new Padding(8) };
    private readonly Label account = new() { AutoSize = true, Text = "尚未登录 GitHub", Padding = new Padding(8, 10, 8, 0) };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label schedule = new() { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(145,171,175), Text = "只连接 GitHub · 每 30 分钟自动检查一次 · 查看和审核按需请求 · 关闭后留在托盘" };
    private readonly Button login = new ReviewButton() { Text = "登录 GitHub", AutoSize = true };
    private readonly Button logout = new ReviewButton() { Text = "退出登录", AutoSize = true };
    private readonly Button refresh = new ReviewButton() { Text = "立即检查", AutoSize = true };
    private readonly Button inspect = new ReviewButton() { Text = "查看投稿", AutoSize = true };
    private readonly Button checks = new ReviewButton() { Text = "查看校验", AutoSize = true };
    private readonly Button merge = new ReviewButton() { Text = "同意并上架", AutoSize = true };
    private readonly Button cancelButton = new ReviewButton() { Text = "取消操作", AutoSize = true, Enabled = false };
    private readonly CheckBox startup = new() { Text = "开机自启动", AutoSize = true, Padding = new Padding(8,8,0,0) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? active;
    private ReviewSnapshot? snapshot;
    private string? user;
    private bool exiting;
    private bool loadingList;
    private bool background;
    private DateTimeOffset? lastSuccess;
    internal ReviewWindow(bool background, bool selfTest = false)
    {
        this.background = background;
        if (background) Opacity = 0;
        settings = selfTest ? new ReviewSettings() : ReviewSettings.Load();
        Text = "RimWise · 排序市场审核助手"; Size = new Size(1160, 840); MinimumSize = new Size(880, 680); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 29, 33); ForeColor = Color.FromArgb(226, 235, 236); Font = new Font("Microsoft YaHei UI", 10);
        DoubleBuffered = true; Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 85)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = false };
        toolbar.Controls.AddRange([login, logout, refresh, startup, account]);
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(schedule, 0, 1);
        var split = new SplitContainer { Size = new Size(1000, 500), Dock = DockStyle.Fill, SplitterDistance = 330, FixedPanel = FixedPanel.Panel1, BackColor = Color.FromArgb(43,60,65) };
        split.Panel1.Controls.Add(list);
        split.Panel1.Padding = new Padding(1); split.Panel2.Padding = new Padding(1);
        var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(14) };
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 235));
        detail.Controls.Add(text, 0, 0); detail.Controls.Add(images, 0, 1); split.Panel2.Controls.Add(detail); layout.Controls.Add(split, 0, 2);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));footer.RowStyles.Add(new RowStyle(SizeType.Percent,100));footer.Controls.Add(status,0,0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false }; actions.Controls.AddRange([cancelButton, merge, inspect, checks]);footer.Controls.Add(actions,0,1);layout.Controls.Add(footer,0,3); Controls.Add(layout);
        ApplyTheme(this);
        merge.BackColor = Color.FromArgb(66,174,161); merge.ForeColor = Color.FromArgb(8,29,29);
        var menu = new ContextMenuStrip(); menu.Items.Add("打开审核助手", null, (_, _) => Reveal());
        menu.Items.Add("立即检查", null, async (_, _) => await RunAsync(CheckAsync));
        menu.Items.Add("退出", null, (_, _) => { exiting = true; Close(); });
        tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "RimWise 市场审核助手", ContextMenuStrip = menu, Visible = !selfTest };
        tray.DoubleClick += (_, _) => Reveal(); tray.BalloonTipClicked += (_, _) => Reveal();
        startup.Checked = !selfTest && ReviewSettings.StartupEnabled(); startup.CheckedChanged += (_, _) => { if(selfTest)return; try { ReviewSettings.SetStartup(startup.Checked); status.Text = startup.Checked ? "已启用开机自启动。" : "已关闭开机自启动。"; } catch (Exception) { status.Text = "自启动设置未能修改，请检查本地权限。"; } };
        login.Click += async (_, _) => await RunAsync(LoginAsync);
        logout.Click += (_, _) => { if (active is not null) return; ReviewCredentials.Delete(); api.SetToken(null);user=null;snapshot=null;list.Items.Clear();ClearImages();text.Clear();SetControls();status.Text="已退出登录，自动检查暂停。"; };
        refresh.Click += async (_, _) => await RunAsync(CheckAsync);
        inspect.Click += async (_, _) => await RunAsync(InspectAsync);
        checks.Click += (_, _) => { if (list.SelectedItem is ReviewPull pull) Process.Start(new ProcessStartInfo($"https://github.com/{ReviewApi.Repository}/pull/{pull.Number}/checks") { UseShellExecute = true }); };
        list.DoubleClick += async (_, _) => await RunAsync(InspectAsync);
        list.SelectedIndexChanged += (_, _) => { if (loadingList) return; snapshot=null;ClearImages();text.Clear();SetControls(); };
        merge.Click += async (_, _) => await RunAsync(async token =>
        {
            if (snapshot is null) return;
            var approved = snapshot;
            status.Text="正在核对提交和可信校验，并请求合并…";
            await api.MergeAsync(approved,token);
            snapshot=null;text.Text="GitHub 已确认合并。市场目录将在 Actions 生成后更新，用户刷新市场即可查看。";ClearImages();
            tray.ShowBalloonTip(7000,"投稿已合并","已合并投稿 #"+approved.Pull.Number+"，等待市场目录生成。",ToolTipIcon.Info);
            await CheckAsync(token); status.Text="已合并，市场目录生成后生效。";
        });
        cancelButton.Click += (_, _) => active?.Cancel();
        timer.Tick += async (_, _) =>
        {
            if (active is null && user is not null && DateTimeOffset.UtcNow >= settings.NextCheckUtc) await RunAsync(CheckAsync);
            else if (active is null && user is not null) schedule.Text=$"只连接 GitHub · 下次自动检查 {settings.NextCheckUtc.ToLocalTime():HH:mm:ss} · 当前暂停 API 请求 · 查看和审核按需请求";
        };
        Shown += async (_, _) =>
        {
            if(selfTest)return;
            if (this.background) { Hide(); Opacity=1; this.background=false; }
            await RunAsync(async token =>
            {
                var saved=ReviewCredentials.Read();
                if (saved is null) { status.Text="请登录维护者 GitHub 账号。登录后即可接收待审提醒。"; return; }
                api.SetToken(saved); user=await api.IdentityAsync(token);account.Text="GitHub · "+user;
                if (DateTimeOffset.UtcNow >= settings.NextCheckUtc) await CheckAsync(token);
            });
            timer.Start();
        };
        FormClosing += (_, e) =>
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel=true;Hide();return; }
            lifetime.Cancel(); active?.Cancel();timer.Stop();tray.Visible=false;
        };
        SetControls();
        text.Text = "登录维护者 GitHub 账号并检查投稿后，双击左侧条目查看简介、更新日志和最多三张图片。\r\n\r\n只展示纯文本，不运行投稿代码。\r\n\r\n确认内容合规后点击“同意并上架”，软件将核对可信校验并直接合并对应提交。";
    }
    private static void ApplyTheme(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            control.ForeColor=Color.FromArgb(226,235,236);control.BackColor=Color.FromArgb(25,38,43);
            if(control is Button button) {button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderColor=Color.FromArgb(65,94,98);button.Padding=new Padding(9,6,9,6);button.Margin=new Padding(0,0,8,6);}
            ApplyTheme(control);
        }
    }
    private void SetControls()
    {
        bool idle=active is null;
        login.Enabled=idle&&user is null;logout.Enabled=refresh.Enabled=idle&&user is not null;
        list.Enabled=idle;inspect.Enabled=idle&&user is not null&&list.SelectedItem is ReviewPull;
        checks.Enabled=idle&&list.SelectedItem is ReviewPull;
        merge.Enabled=idle&&user is not null&&snapshot is { Supported:true };cancelButton.Enabled=!idle;
    }
    private async Task RunAsync(Func<CancellationToken,Task> work)
    {
        if(active is not null || exiting) return;
        using var source=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);active=source;SetControls();
        try {await work(source.Token);}
        catch(OperationCanceledException) {if(!exiting)status.Text="操作已取消；若已发出合并，请刷新核对 GitHub 状态。";}
        catch(Exception ex) {if(!exiting)status.Text=(ex is InvalidOperationException or InvalidDataException or MarketplaceException) && ex.Message.Any(c=>c is >= '\u3400' and <= '\u9fff') ? ex.Message : "操作未完成，请检查网络或登录状态后重试。";}
        finally {active=null;if(!exiting)SetControls();}
    }
    private async Task LoginAsync(CancellationToken token)
    {
        var clientId=Program.Metadata("GitHubOAuthClientId");var secret=Program.Metadata("GitHubOAuthClientSecret");
        if(string.IsNullOrWhiteSpace(secret))throw new InvalidOperationException("此构建尚未配置 GitHub 登录，请使用维护者配置的完整构建。");
        using var auth=new GitHubLoopbackAuthorization(clientId,secret);
        status.Text="请在浏览器完成 GitHub 授权，完成后会自动返回。";
        var granted=await auth.AuthorizeAsync((uri,cancel)=>{cancel.ThrowIfCancellationRequested();Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});return Task.CompletedTask;},token);
        api.SetToken(granted.AccessToken);
        try {user=await api.IdentityAsync(token);token.ThrowIfCancellationRequested();ReviewCredentials.Write(granted.AccessToken);}
        catch {api.SetToken(null);user=null;throw;}
        account.Text="GitHub · "+user;await CheckAsync(token);
    }
    private async Task CheckAsync(CancellationToken token)
    {
        if(user is null)throw new InvalidOperationException("请先登录 GitHub。");
        settings.LastCheckUtc=DateTimeOffset.UtcNow;settings.Save();status.Text="正在检查 GitHub 待审投稿…";
        var pulls=await api.ListAsync(token);token.ThrowIfCancellationRequested();
        var changed=settings.Notifications.Update(pulls);settings.Save();
        int? selected=(list.SelectedItem as ReviewPull)?.Number;
        loadingList=true;try {list.Items.Clear();foreach(var pull in pulls)list.Items.Add(pull);if(selected is not null)list.SelectedItem=pulls.FirstOrDefault(p=>p.Number==selected);}
        finally {loadingList=false;}
        if(snapshot is not null&&!pulls.Any(p=>p.Number==snapshot.Pull.Number&&p.Sha==snapshot.Pull.Sha)) {snapshot=null;ClearImages();text.Text="所查看的投稿已更新或关闭，请重新查看。";}
        if(changed.Count>0)tray.ShowBalloonTip(10000,"有新的市场投稿待审核",$"{changed.Count} 份投稿新增或更新，点击打开审核助手。",ToolTipIcon.Info);
        lastSuccess=DateTimeOffset.Now;status.Text=$"{lastSuccess:HH:mm} 检查完成 · 待审 {pulls.Count} 项 · 30 分钟后再次检查";
    }
    private async Task InspectAsync(CancellationToken token)
    {
        if(list.SelectedItem is not ReviewPull pull)return;
        snapshot=null;ClearImages();status.Text="正在读取投稿文本和图片…";
        var loaded=await api.InspectAsync(pull.Number,token);token.ThrowIfCancellationRequested();
        RenderSnapshot(loaded);
        if (loaded.Supported)
        {
            status.Text="投稿已读取，正在核对市场校验状态…";
            await api.CheckReadinessAsync(loaded.Pull, token);
            status.Text="市场可信校验已通过。请确认图片和文字合规后点击“同意并上架”。";
        }
    }
    internal void PreviewForChecks(ReviewSnapshot value) { list.Items.Add(value.Pull); RenderSnapshot(value); SetControls(); }
    private void RenderSnapshot(ReviewSnapshot loaded)
    {
        try
        {
            foreach(var image in loaded.Images)
            {
                using var input=new MemoryStream(image.Content,false);using var original=Image.FromStream(input,true,true);
                var copy=new Bitmap(original);var panel=new PictureBox{Width=250,Height=205,SizeMode=PictureBoxSizeMode.Zoom,Image=copy,Margin=new Padding(0,0,10,0)};
                panel.DoubleClick+=(_,_)=>{using var viewer=new Form{Text="投稿图片 · 双击关闭",Width=950,Height=750,BackColor=BackColor,StartPosition=FormStartPosition.CenterParent};var picture=new PictureBox{Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,Image=copy};picture.DoubleClick+=(_,_)=>viewer.Close();viewer.Controls.Add(picture);viewer.ShowDialog(this);};images.Controls.Add(panel);
            }
            text.Text=loaded.Text; snapshot=loaded; status.Text=loaded.Warning;
        }
        catch {ClearImages();throw new InvalidDataException("图片无法完整解码，已禁用合并。请让作者更换图片。");}
    }
    private void ClearImages(){foreach(var child in images.Controls.Cast<Control>().ToArray()){if(child is PictureBox p)p.Image?.Dispose();images.Controls.Remove(child);child.Dispose();}}
    private void Reveal(){Show();WindowState=FormWindowState.Normal;Activate();}
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);int dark=1;_ = DwmSetWindowAttribute(Handle,20,ref dark,sizeof(int));
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    protected override void Dispose(bool disposing)
    {
        if(disposing){timer.Dispose();tray.Dispose();ClearImages();api.Dispose();lifetime.Dispose();}base.Dispose(disposing);
    }
}
