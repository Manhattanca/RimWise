using System.Reflection;

namespace RimWise.MarketplaceReview;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            if (Application.OpenForms.OfType<ReviewWindow>().FirstOrDefault() is { } window) window.HandleUnexpectedUiError(e.Exception);
            else MessageBox.Show("审核助手界面发生错误，请重新打开并核对服务器状态。", "RimWise 审核助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        try
        {
            if (args.Length == 1 && args[0] == "--enable-startup") { ReviewSettings.SetStartup(true); return 0; }
            if (args.Length == 1 && args[0] == "--disable-startup") { ReviewSettings.SetStartup(false); return 0; }
            if (args.Length == 2 && args[0] == "--self-test") return ReviewSelfTest.Run(args[1]).GetAwaiter().GetResult();
            using var mutex = new Mutex(true, @"Local\RimWise.MarketplaceReview", out bool first);
            if (!first) { MessageBox.Show("审核助手已在运行，请从系统托盘打开。", "RimWise 审核助手"); return 0; }
            using var form = new ReviewWindow(args.Contains("--background"));
            Application.Run(form); return 0;
        }
        catch (Exception) { MessageBox.Show("审核助手未能启动，请检查本地设置、凭据或文件权限。", "RimWise 审核助手", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
    internal static string Metadata(string key) => typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "";
}
