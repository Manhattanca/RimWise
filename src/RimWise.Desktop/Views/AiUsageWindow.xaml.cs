using System.Windows;
using RimWise.Integration.AI;

namespace RimWise.Desktop.Views;

public partial class AiUsageWindow : Window
{
    public AiUsageWindow(AiTokenEstimate estimate)
    {
        InitializeComponent();
        EstimateText.Text = estimate.Description;
        BreakdownText.Text = $"预计输入 {estimate.Input:N0} token，输出 {estimate.Output:N0} token。";
    }
}
