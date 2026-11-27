using System.IO;
using System.Windows;
using RimWise.Desktop.Services;
using RimWise.Integration.Diagnostics;

namespace RimWise.Desktop.Views;

public partial class AiSortingWindow
{
    private void MarkAutomaticSortingUsed()
    {
        try { SortingGuidePreferences.MarkAutoSortUsed(dataDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DiagnosticEventLog.Current?.Record("sorting-guide", "preference-save-failed", "used: " + ex.Message); }
    }
}
