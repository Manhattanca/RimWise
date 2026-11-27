using RimWise.Desktop.Services;
using Forms = System.Windows.Forms;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private async Task ObserveMarketNoticesAsync(CancellationToken token)
    {
        var shown = new HashSet<Guid>();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try
                {
                    var market = LocalMarket;
                    if (market.ActiveSession is not long session) continue;
                    var notices = await market.ReadNoticesAsync(token);
                    foreach (var notice in notices)
                    {
                        token.ThrowIfCancellationRequested();
                        if (market.ActiveSession != session) break;
                        if (!shown.Contains(notice.Id))
                        {
                            if (!await ShowMarketNoticeAsync(notice, token)) break;
                            shown.Add(notice.Id);
                        }
                        if (market.ActiveSession != session) break;
                        await market.AckNoticeAsync(notice.Id, token);
                        shown.Remove(notice.Id);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { if (token.IsCancellationRequested) return; /* Leave unread on network or shell failure. */ }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task<bool> ShowMarketNoticeAsync(LocalMarketApiClient.Notice notice, CancellationToken token,
        Action<Forms.NotifyIcon, string>? display = null)
    {
        token.ThrowIfCancellationRequested();
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        using var tray = new Forms.NotifyIcon { Icon = icon ?? System.Drawing.SystemIcons.Information, Text = "RimWise 排序市场", Visible = true };
        // Window shutdown can stop the dispatcher before the async finally runs.
        using var removeOnClose = token.Register(tray.Dispose);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tray.BalloonTipShown += (_, _) => delivered.TrySetResult();
        var text = $"《{notice.Title}》 {notice.Version}\n退回原因：{notice.Reason}";
        if (display is null) tray.ShowBalloonTip(8000, "排序市场：投稿已退回", text.Length <= 250 ? text : text[..220] + "…（完整原因见我的排序）", Forms.ToolTipIcon.Warning);
        else display(tray, text);
        try { await delivered.Task.WaitAsync(TimeSpan.FromSeconds(15), token); }
        catch (TimeoutException) { return false; }
        await Task.Delay(8000, token);
        return true;
    }
#if DEBUG
    internal void CheckCloseWhileNoticePending(string folder)
    {
        bool released = false;
        var pending = ShowMarketNoticeAsync(new(Guid.NewGuid(), "fixture", "1.0", "fixture"), lifetime.Token,
            (tray, _) => tray.Disposed += (_, _) => released = true);
        if (pending.IsCompleted) throw new InvalidOperationException("Expected pending shell delivery");
        Closed += (_, _) => System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "notice-close.txt"),
            released && lifetime.IsCancellationRequested ? "released" : "failed");
        Close();
    }
#endif
}
