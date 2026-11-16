#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;

namespace RimWise.Desktop;

/// <summary>Isolated order-import regression, independent of unrelated rendering/AI estimate checks.</summary>
internal static class OrderImportSmokeCheck
{
    internal static void Attach(MainWindow window, string output)
    {
        window.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            var checks = new List<string>();
            try
            {
                window.LoadSample();
                await MarketplaceApplySmokeCheck.RunAsync(window, output, checks);
                await ImportAndThumbnailSmokeCheck.RunAsync(window, output, checks);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks }));
                window.ViewModel.MarkExported();
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = false, Checks = checks }));
                window.ViewModel.MarkExported();
                Application.Current.Shutdown(1);
            }
        };
    }
}
#endif
