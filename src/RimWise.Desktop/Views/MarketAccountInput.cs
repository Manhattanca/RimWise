using System.Windows;
using System.Windows.Controls;

namespace RimWise.Desktop.Views;

internal static class MarketAccountInput
{
    internal static double RowHeight => (double)Application.Current.Resources["SearchInputHeight"];

    internal static void ApplyCompactLayout(Control input)
    {
        input.Height = RowHeight;
        input.Padding = new Thickness(0);
        input.VerticalContentAlignment = VerticalAlignment.Center;
    }
}
