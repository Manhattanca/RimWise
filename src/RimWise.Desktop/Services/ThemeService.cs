using System.IO;
using System.Security;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Services;

/// <summary>Keeps Fluent controls and our palette in sync without recreating windows.</summary>
internal sealed class ThemeService : IDisposable
{
    private readonly Application application;
    private readonly AppearanceSettingsStore store;
    private readonly Func<bool> systemIsDark;
    private ResourceDictionary? palette;
    public ThemePreference Preference { get; private set; } = ThemePreference.System;
    public bool IsDark { get; private set; }
    public string? LoadWarning { get; private set; }
    public event EventHandler? Changed;

    internal ThemeService(Application application, AppearanceSettingsStore store, Func<bool>? systemIsDark = null)
    {
        this.application = application;
        this.store = store;
        this.systemIsDark = systemIsDark ?? ReadSystemIsDark;
        try { Preference = store.Load(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { LoadWarning = "无法读取外观配置，已跟随系统"; }
        Apply();
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    public void Select(ThemePreference preference, bool save = true)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        // A failed save leaves the selection and active theme unchanged.
        if (save) store.Save(preference);
        Preference = preference;
        Apply();
    }

    internal void RefreshSystemTheme()
    {
        if (Preference == ThemePreference.System) Apply();
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!application.Dispatcher.HasShutdownStarted)
            application.Dispatcher.BeginInvoke(RefreshSystemTheme);
    }

    private void Apply()
    {
        application.Dispatcher.VerifyAccess();
        IsDark = Preference == ThemePreference.Dark || Preference == ThemePreference.System && systemIsDark();
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"/RimWise;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative)
        };
        // .NET 10 still marks the documented runtime switch as experimental.
        // Keep its use here so the rest of the UI has no dependency on that API.
#pragma warning disable WPF0001
        application.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        var dictionaries = application.Resources.MergedDictionaries;
        if (palette is not null) dictionaries.Remove(palette);
        dictionaries.Add(replacement);
        palette = replacement;
        foreach (Window window in application.Windows) WindowFrameTheme.Apply(window);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return false; }
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        if (palette is not null) application.Resources.MergedDictionaries.Remove(palette);
    }
}
