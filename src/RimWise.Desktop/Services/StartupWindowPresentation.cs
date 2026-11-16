using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Integration.Diagnostics;

namespace RimWise.Desktop.Services;

/// <summary>Keeps the native window off screen until WPF has submitted its first scene.</summary>
internal sealed class StartupWindowPresentation
{
    private readonly Window window;
    private readonly Func<IntPtr, bool, int> setCloaked;
    private readonly Func<int> flush;
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr handle;
    private bool cloaked;
    private bool finished;
    private DispatcherOperation? reveal;
    internal Task<bool> Ready => ready.Task;
#if DEBUG
    internal event Action? BeforeReveal;
#endif

    internal StartupWindowPresentation(Window window, Func<IntPtr, bool, int>? setCloaked = null, Func<int>? flush = null)
    {
        this.window = window;
        this.setCloaked = setCloaked ?? SetCloaked;
        this.flush = flush ?? DwmFlush;
        window.SourceInitialized += OnSourceInitialized;
        window.ContentRendered += OnContentRendered;
        window.Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        handle = new WindowInteropHelper(window).Handle;
        // Unlike opacity zero, DWM cloaking leaves composition running. The normal
        // opaque HWND, layout, DPI and native caption remain unchanged.
        var result = setCloaked(handle, true);
        cloaked = result >= 0;
        if (!cloaked) Record("cloak-unavailable", result);
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        window.ContentRendered -= OnContentRendered;
        if (!cloaked) { Complete(); return; }
        // ContentRendered is a dispatcher notification, not a DWM present fence.
        // Allow the following render pass to submit the scene, then synchronize DWM.
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        reveal = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(Complete));
    }

    private void Complete()
    {
        if (finished) return;
        try
        {
            if (cloaked)
            {
                var result = flush();
                if (result < 0) Record("flush-unavailable", result);
            }
#if DEBUG
            BeforeReveal?.Invoke();
#endif
        }
        finally
        {
            if (!finished)
            {
                var released = true;
                if (cloaked)
                {
                    var result = setCloaked(handle, false);
                    released = result >= 0;
                    if (!released) Record("uncloak-failed", result);
                    cloaked = false;
                }
                finished = true;
                Detach();
                ready.TrySetResult(released);
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (finished) return;
        finished = true;
        // Closed means the HWND has been destroyed; no native call may outlive it.
        reveal?.Abort();
        Detach();
        ready.TrySetResult(false);
    }

    private void Detach()
    {
        window.SourceInitialized -= OnSourceInitialized;
        window.ContentRendered -= OnContentRendered;
        window.Closed -= OnClosed;
        CompositionTarget.Rendering -= OnRendering;
    }

    private static int SetCloaked(IntPtr handle, bool enabled)
    {
        var value = enabled ? 1 : 0;
        return DwmSetWindowAttribute(handle, 13 /* DWMWA_CLOAK */, ref value, sizeof(int));
    }

    private static void Record(string stage, int result) =>
        DiagnosticEventLog.Current?.Record("interface", "startup-presentation-" + stage, $"hresult={result}");

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
