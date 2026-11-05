using System.Diagnostics;
using System.Text;
using RimWise.Integration.Diagnostics;

Console.OutputEncoding = Encoding.UTF8;
bool interactive = args.Length == 0;
try
{
    if (args.Length != 0 && args.Length != 4) throw new ArgumentException();
    if (args.Length == 4 && (args[0] != "--profile" || args[2] != "--output")) throw new ArgumentException();
    Console.WriteLine("RimWise 独立诊断导出\n无需打开主程序，可在主程序卡住或退出后使用。\n正在收集并脱敏日志，请稍候…");
    string? profile = null, discoveryFailure = null;
    try { profile = interactive ? EmergencyDiagnostics.ResolveProfile(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimWise")) : Path.GetFullPath(args[1]); }
    catch (Exception ex) { discoveryFailure = ex.GetType().Name; }
    var destination = interactive ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        $"RimWise-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip") : Path.GetFullPath(args[3]);
    var full = EmergencyDiagnostics.Export(profile, destination, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), discoveryFailure);
    Console.WriteLine(full ? "诊断包已生成，包含可用日志及 AI token 用量。" : "已生成精简诊断包：部分配置或密钥无法读取，原始日志已排除，具体缺失见 manifest.json。");
    Console.WriteLine("文件：" + destination + "\n请将 ZIP 提供给开发者，并说明操作步骤和故障时间。此工具不会上传文件。");
    if (interactive)
    {
        try { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select,"); start.ArgumentList.Add(destination); Process.Start(start); }
        catch { Console.WriteLine("未能打开文件夹，请使用上面的路径查找文件。"); }
    }
}
catch (Exception ex)
{
    Console.WriteLine("无法保存诊断包（" + ex.GetType().Name + "）。请检查输出位置是否可写、磁盘空间是否充足。\n命令行用法：RimWise.Diagnostics.exe --profile 用户数据目录 --output 诊断ZIP路径");
    Environment.ExitCode = 1;
}
if (interactive) { Console.WriteLine("按回车关闭。"); Console.ReadLine(); }
