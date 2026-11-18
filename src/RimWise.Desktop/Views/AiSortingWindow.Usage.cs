using RimWise.Integration.Diagnostics;

namespace RimWise.Desktop.Views;

public partial class AiSortingWindow
{
    private Task usageRefreshTask = Task.CompletedTask;
    private bool usageRefreshRequested;
    private string? displayedUsage;

    private void RefreshUsage() => _ = RefreshUsageAsync();

    internal Task RefreshUsageAsync()
    {
        if (closed) return Task.CompletedTask;
        if (IsOfflineMode)
        {
            TokenText.Text = "本次离线排序：0 token（未调用 AI）。";
            return Task.CompletedTask;
        }
        // Ledger verification remains authoritative and complete, but never runs on the
        // dispatcher. Coalesce repeated refreshes into one reader and one pending reread.
        usageRefreshRequested = true;
        if (usageRefreshTask.IsCompleted) usageRefreshTask = ReadUsageAsync();
        return usageRefreshTask;
    }

    private async Task ReadUsageAsync()
    {
        while (usageRefreshRequested && !closed)
        {
            usageRefreshRequested = false;
            var originalText = TokenText.Text;
            var read = (Func<string>)session.UsageDescription;
#if DEBUG
            read = ReadUsageForCheck ?? read;
#endif
            string description;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { description = await Task.Run(read); }
            catch (Exception ex) when (IsExpectedError(ex) || ex is OverflowException or System.Security.SecurityException)
            {
                description = "累计用量读取失败，记录已保留。";
                DiagnosticEventLog.Current?.Record("automatic-sorting", "usage-read-failed", ex.GetType().Name);
            }
            if (watch.ElapsedMilliseconds >= 1000)
                DiagnosticEventLog.Current?.Record("automatic-sorting", "usage-read-slow",
                    $"elapsedMilliseconds={watch.ElapsedMilliseconds}");
            // A finished reader must not overwrite a newer run's token/progress text,
            // offline mode, or a disposed embedded view. Never wait for disk on close.
            if (closed || usageRefreshRequested || IsOfflineMode || requestCancellation is not null
                || TokenText.Text != originalText) continue;
            var prefix = originalText;
            if (displayedUsage is not null)
            {
                if (prefix == displayedUsage) prefix = "";
                else if (prefix.EndsWith("\n" + displayedUsage, StringComparison.Ordinal))
                    prefix = prefix[..^(displayedUsage.Length + 1)];
            }
            TokenText.Text = prefix + (prefix.Length == 0 ? "" : "\n") + description;
            displayedUsage = description;
        }
    }

#if DEBUG
    internal Func<string>? ReadUsageForCheck { get; set; }
#endif
}
