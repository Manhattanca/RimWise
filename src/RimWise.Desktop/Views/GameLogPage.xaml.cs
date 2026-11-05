using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Game;
using RimWise.Integration.Diagnostics;

namespace RimWise.Desktop.Views;

public partial class GameLogPage : UserControl, IDisposable
{
    private sealed class SourceState
    {
        public LiveGameLog? Reader;
        public readonly List<LiveLogLine> Lines = [];
        public LiveLogBatch? Batch;
        public string Status = "等待读取日志。";
        public bool Manual;
    }
    private readonly SourceState[] sources = [new(), new()];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? analysis;
    private readonly Func<(string Startup, string Runtime)> locate;
    private readonly Func<ModCatalogue?> catalogue;
    private bool ready, reading, paused, disposed;
    private int generation;
    private int Index => RuntimeTab.IsChecked == true ? 1 : 0;
    private SourceState Current => sources[Index];
    private const int DisplayLines = 12000;

    public GameLogPage(Func<(string Startup, string Runtime)> locate, Func<ModCatalogue?> catalogue)
    {
        this.locate = locate; this.catalogue = catalogue;
        InitializeComponent(); ready = true;
        timer.Tick += async (_, _) => await PollAsync();
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RenderLines(); };
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && !disposed) { LoadAutomatic(); timer.Start(); await PollAsync(); }
            else timer.Stop();
        };
    }
    private void LoadAutomatic()
    {
        try
        {
            var paths = locate();
            SetAutomatic(0, paths.Startup); SetAutomatic(1, paths.Runtime);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        { Current.Status = "未能定位日志：" + ex.Message; Render(); }
    }
    private void SetAutomatic(int index, string path)
    {
        var state = sources[index];
        if (state.Manual || string.Equals(state.Reader?.Path, path, StringComparison.OrdinalIgnoreCase)) return;
        state.Reader = new(path); state.Lines.Clear(); state.Batch = null;
        generation++;
    }
    private async Task PollAsync(bool force = false)
    {
        if (disposed || reading || paused && !force || !IsVisible) return;
        reading = true;
        var state = Current; var reader = state.Reader;
        try
        {
            if (reader is null) { LoadAutomatic(); return; }
            var batch = await Task.Run(() => reader.Read(lifetime.Token));
            // The reader has advanced even if the user switched tabs during the read.
            // Commit to its own source cache, then render only the currently shown source.
            if (disposed || reader != state.Reader) return;
            if (batch.Reset) state.Lines.Clear();
            state.Lines.AddRange(batch.Lines);
            if (state.Lines.Count > DisplayLines) state.Lines.RemoveRange(0, state.Lines.Count - DisplayLines);
            state.Batch = batch;
            state.Status = batch.HasMore ? "正在增量读取…" : paused ? "读取已暂停。" : "实时读取中；未换行的末行会在写入完成后显示。";
            if (state == Current) Render(batch.Reset || batch.Lines.Count > 0);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            state.Status = ex is FileNotFoundException or DirectoryNotFoundException
                ? "此日志尚未生成，等待文件出现。若游戏不生成独立日志，请查看启动日志或选择文件。"
                : "读取暂不可用，将自动重试：" + ex.Message;
            if (!disposed && state == Current && reader == state.Reader) Render(false);
        }
        finally { reading = false; }
    }
    private void Render(bool lines = true)
    {
        if (!ready) return;
        var state = Current;
        FileInfoText.Text = state.Reader?.Path ?? "未选择文件";
        FileInfoLink.IsEnabled = state.Reader is not null;
        FileStats.Text = state.Batch is { } batch ? $"大小：{batch.FileBytes:N0} 字节\n更新：{batch.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n已读：{batch.BytesRead:N0} 字节" : "等待文件信息";
        var counts = state.Batch?.Counts;
        TotalCountText.Text = state.Batch is { } b ? $"已读行：{b.TotalLines:N0}" : "暂无日志";
        InfoCountText.Text = $"信息 {counts?[0] ?? 0:N0}";
        WarningCountText.Text = $"警告 {counts?[1] ?? 0:N0}";
        ErrorCountText.Text = $"错误 {counts?[2] ?? 0:N0}";
        ExceptionCountText.Text = $"异常 {counts?[3] ?? 0:N0}";
        var shared = Index == 1 && !state.Manual && string.Equals(state.Reader?.Path, sources[0].Reader?.Path, StringComparison.OrdinalIgnoreCase)
            ? "未找到独立游戏中日志，当前实时跟踪 Player 日志（包含启动与运行内容）。 " : "";
        ReadStatus.Text = shared + state.Status + " 界面保留最近 12,000 行供搜索，原文件完整保留。";
        if (lines) RenderLines();
    }
    private void RenderLines()
    {
        if (!ready || disposed) return;
        var selected = LinesList.SelectedItem as LiveLogLine;
        var scroll = FindScrollViewer(LinesList);
        var scrollOffset = scroll?.VerticalOffset ?? 0;
        var oldLines = LinesList.ItemsSource as LiveLogLine[];
        var anchor = oldLines is { Length: > 0 } ? oldLines[Math.Clamp((int)scrollOffset, 0, oldLines.Length - 1)].Number : -1;
        var text = SearchText.Text.Trim();
        var level = LevelFilter.SelectedIndex;
        var visible = Current.Lines.Where(line => (text.Length == 0 || line.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
            && (level == 0 || level == 1 && line.Level is LiveLogLevel.Error or LiveLogLevel.Exception
                || level == 2 && line.Level == LiveLogLevel.Warning || level == 3 && line.Level == LiveLogLevel.Information)).ToArray();
        LinesList.ItemsSource = visible;
        if (selected is not null && visible.Contains(selected)) LinesList.SelectedItem = selected;
        if (FollowTail.IsChecked == true && visible.Length > 0) LinesList.ScrollIntoView(visible[^1]);
        else if (anchor >= 0 && scroll is not null)
        {
            var position = Array.FindIndex(visible, line => line.Number >= anchor);
            scroll.ScrollToVerticalOffset(Math.Max(0, position));
        }
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
    private async void Source_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        generation++; analysis?.Cancel(); AnalysisPanel.Visibility = Visibility.Collapsed;
        DetailText.Clear(); Render(); await PollAsync();
    }
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (ready) { searchTimer.Stop(); searchTimer.Start(); } }
    private void Line_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (LinesList.SelectedItem is not LiveLogLine line) return;
        DetailText.Text = $"第 {line.Number} 行 · {line.LevelLabel}\n{line.Text}";
        DetailText.SetResourceReference(Control.ForegroundProperty, line.Level switch
        {
            LiveLogLevel.Warning => "WarningTextBrush",
            LiveLogLevel.Error or LiveLogLevel.Exception => "ErrorTextBrush",
            _ => "TextBrush"
        });
    }
    private async void AutoLoad_Click(object sender, RoutedEventArgs e) { Current.Manual = false; LoadAutomatic(); Render(); await PollAsync(true); }
    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择" + (Index == 0 ? "启动" : "游戏中") + "日志", Filter = "日志文件|*.log;*.txt|所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        SelectFile(dialog.FileName); await PollAsync(true);
    }
    internal void SelectFile(string path)
    {
        generation++; analysis?.Cancel(); AnalysisPanel.Visibility = Visibility.Collapsed;
        Current.Manual = true; Current.Reader = new(path); Current.Lines.Clear(); Current.Batch = null;
        DetailText.Clear(); Render();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await PollAsync(true);
    private void Pause_Click(object sender, RoutedEventArgs e)
    { paused = !paused; PauseButton.Content = paused ? "继续读取" : "暂停读取"; Current.Status = paused ? "读取已暂停。" : "实时读取中。"; Render(false); }
    private void Previous_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(1);
    private void Navigate(int delta)
    {
        if (LinesList.Items.Count == 0) return;
        FollowTail.IsChecked = false;
        LinesList.SelectedIndex = Math.Clamp(LinesList.SelectedIndex + delta, 0, LinesList.Items.Count - 1);
        LinesList.ScrollIntoView(LinesList.SelectedItem);
    }
    private void End_Click(object sender, RoutedEventArgs e)
    { if (LinesList.Items.Count > 0) LinesList.ScrollIntoView(LinesList.Items[LinesList.Items.Count - 1]); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetDirectoryName(Current.Reader?.Path);
            if (folder is not null && Directory.Exists(folder)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { ReadStatus.Text = ex.Message; }
    }
    private void RevealFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Current.Reader is not { } reader) return;
            var path = Path.GetFullPath(reader.Path);
            if (!File.Exists(path))
            {
                OpenFolder_Click(sender, e);
                ReadStatus.Text = "日志文件尚未生成或已被移走。";
                return;
            }
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { ReadStatus.Text = "无法打开日志位置：" + ex.Message; }
    }
    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (analysis is not null || Current.Reader is not { } reader) return;
        analysis = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var cancellation = analysis; var revision = generation; var kind = Index == 0 ? "startup" : "game-errors";
        AnalyzeButton.IsEnabled = false; CancelAnalysisButton.IsEnabled = true;
        AnalysisPanel.Visibility = Visibility.Visible; AnalysisText.Text = "正在读取日志快照并核对来源…";
        try
        {
            var settings = new AiSettingsStore(UserStorage.DirectoryPath).Load();
            var key = new LocalEncryptedSecretStore(UserStorage.DirectoryPath).Read(settings.SecretName);
            var mods = catalogue() ?? new ModCatalogue([]);
            var request = await Task.Run(() => GameLogAnalysis.CreateRequest(PlayerLogReader.Read(reader.Path, mods) with { Kind = kind },
                mods, settings, key, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var snapshotTime = DateTime.Now;
            if (revision == generation) AnalysisText.Text = "AI 正在分析来源…";
            using var client = new AiClient();
            var result = await GameLogAnalysis.AnalyzeAsync(request, UserStorage.DirectoryPath, client, cancellation.Token);
            if (disposed || revision != generation) return;
            var content = result.Succeeded ? result.Response?.StartupDiagnosis?.Reason ?? result.Response?.Summary : result.Message;
            AnalysisText.Text = $"{Path.GetFileName(reader.Path)} · {snapshotTime:HH:mm:ss} 快照\n"
                + new DiagnosticRedactor().Text(content ?? "未获得有效结论。")
                + (result.Usage is { Valid: true } usage ? $"\n用量：{usage.Total:N0} token" : "\n服务未回报有效用量，已记录预估。")
                + "\n归属候选不等于已证实根因。后续新增日志不在本次分析内。";
        }
        catch (OperationCanceledException) { if (!disposed && revision == generation) AnalysisText.Text = "分析已取消。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or System.Net.Http.HttpRequestException or TimeoutException)
        { if (!disposed && revision == generation) AnalysisText.Text = new DiagnosticRedactor().Text(ex.Message); }
        finally
        {
            analysis = null; cancellation.Dispose();
            if (!disposed) { AnalyzeButton.IsEnabled = true; CancelAnalysisButton.IsEnabled = false; }
        }
    }
    private void CancelAnalysis_Click(object sender, RoutedEventArgs e) => analysis?.Cancel();
    public void Dispose()
    { if (disposed) return; disposed = true; timer.Stop(); searchTimer.Stop(); lifetime.Cancel(); analysis?.Cancel(); }
#if DEBUG
    internal Task ReadForCheckAsync() => PollAsync(true);
    internal int VisibleCountForCheck => LinesList.Items.Count;
#endif
}
