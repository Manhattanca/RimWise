#if DEBUG
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class OrderImportDialogSmokeCheck
{
    private delegate void TimerCallback(IntPtr window, uint message, UIntPtr timer, uint time);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, TimerCallback callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(IntPtr window, UIntPtr id);
    private delegate bool ChildCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, ChildCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow { void GetWindow(out IntPtr window); void ContextSensitiveHelp(bool enter); }

    private static void SelectNativeType(IntPtr window, int index)
    {
        var found = false;
        EnumChildWindows(window, (child, _) =>
        {
            var name = new System.Text.StringBuilder(128);
            GetClassName(child, name, name.Capacity);
            if (name.ToString() != "ComboBox" || SendMessage(child, 0x146, IntPtr.Zero, IntPtr.Zero).ToInt32() != 6) return true;
            SendMessage(child, 0x14E, index, IntPtr.Zero); // CB_SETCURSEL
            SendMessage(GetParent(child), 0x111, (1 << 16) | (GetDlgCtrlID(child) & 0xffff), child); // CBN_SELCHANGE
            SendMessage(GetParent(child), 0x111, (9 << 16) | (GetDlgCtrlID(child) & 0xffff), child); // CBN_SELENDOK
            found = true;
            return false;
        }, IntPtr.Zero);
        Check(found, "Native file-type selector not found");
    }
    internal static void Run(Window owner, string output)
    {
        var root = Path.Combine(output, "导入路径 [检查] &");
        var config = Path.Combine(root, "游戏数据", "Config");
        var saves = Path.Combine(root, "游戏数据", "Saves");
        var xml = Path.Combine(root, "XML 导出");
        var json = Path.Combine(root, "JSON 导入");
        foreach (var path in new[] { config, saves, xml, json }) Directory.CreateDirectory(path);
        var locations = new OrderDialogLocations(Path.Combine(root, "设置"));
        locations.Remember(Path.Combine(xml, "ModsConfig.xml"), true);
        locations.Remember(Path.Combine(json, "order.json"), false);
        locations = new OrderDialogLocations(Path.Combine(root, "设置"));
        Check(locations.Resolve("xml", config) == xml, "XML export location not persisted");
        Check(locations.Resolve("rws", config) == saves, "Custom game Saves location not selected");
        Check(locations.Resolve("json", config) == json, "JSON location not persisted");
        Check(locations.Resolve("rml", config) is null, "Unknown format unexpectedly redirects");

        Exception? failure = null;
        var steps = 0;
        var ticks = 0;
        UIntPtr timer = UIntPtr.Zero;
        TimerCallback? callback = null;
        void Trace(string value) => File.AppendAllText(Path.Combine(output, "import-dialog-trace.txt"), value + Environment.NewLine);
        Trace("Before native dialog");
        // Real common-dialog event sink and native SetFolder, without opening player files.
        var result = OrderImportDialog.Show(owner, kind => { Trace("Resolve: " + kind); return locations.Resolve(kind, config); }, dialog =>
        {
            Trace("Native dialog configured");
            callback = (_, _, _, _) =>
            {
                try
                {
                    Trace($"Timer {ticks}, step {steps}");
                    if (++ticks > 60) throw new InvalidOperationException("Native folder navigation timed out");
                    ((IOleWindow)dialog).GetWindow(out var nativeWindow);
                    var expected = steps == 0 ? xml : steps == 1 ? saves : json;
                    dialog.GetFolder(out var folder);
                    string actual;
                    try { actual = OrderImportDialog.FilePath(folder); }
                    finally { Marshal.ReleaseComObject(folder); }
                    dialog.GetFileTypeIndex(out var selectedType);
                    Trace($"Current: {selectedType}, {actual}");
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return;
                    steps++;
                    if (steps == 3) { KillTimer(IntPtr.Zero, timer); PostMessage(nativeWindow, 0x10, IntPtr.Zero, IntPtr.Zero); return; }
                    SelectNativeType(nativeWindow, steps == 1 ? 4 : 2);
                }
                catch (Exception ex)
                {
                    failure = ex; Trace(ex.ToString()); KillTimer(IntPtr.Zero, timer);
                    ((IOleWindow)dialog).GetWindow(out var nativeWindow);
                    PostMessage(nativeWindow, 0x10, IntPtr.Zero, IntPtr.Zero);
                }
            };
            dialog.SetFileTypeIndex(2);
            Trace("Type set to XML");
            new OrderImportDialog.Events(kind => locations.Resolve(kind, config)).OnTypeChange(dialog);
            Trace("XML folder applied");
            timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 150, callback);
            Trace("Timer started");
            Check(timer != UIntPtr.Zero, "Native dialog test timer could not start");
        });
        KillTimer(IntPtr.Zero, timer);
        GC.KeepAlive(callback);
        if (failure is not null) throw failure;
        Check(result is null && steps == 3, "Native dialog cancellation/navigation failed");
        Directory.Delete(saves);
        Check(locations.Resolve("rws", config) is null, "Missing Saves should allow manual browsing");
        File.WriteAllText(Path.Combine(output, "import-dialog-checks.txt"),
            "Passed: persisted per-format directories, custom Saves, Unicode paths, native XML/Saves/JSON navigation, cancellation, missing-directory fallback. No player files used.");
    }
    private static void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
}
#endif
