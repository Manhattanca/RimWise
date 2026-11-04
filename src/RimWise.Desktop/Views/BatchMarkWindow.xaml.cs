using System.Windows;

namespace RimWise.Desktop.Views;

public enum BatchMarkAction { None, RetainColor, UnretainColor, Color, ClearColor }

public partial class BatchMarkWindow : Window
{
    public BatchMarkAction Action { get; private set; }
    public string? SelectedColor { get; private set; }

    public BatchMarkWindow(int count, IReadOnlySet<string>? retainedColors = null)
    {
        InitializeComponent();
        SelectionText.Text = $"已选择 {count} 个模组";
        foreach (var color in Presentation.MarkerPalette.Colors)
        {
            var button = new System.Windows.Controls.Button { MinWidth = 36, Width = 36, Height = 32, Padding = new Thickness(4), ToolTip = color.Name,
                Content = new System.Windows.Shapes.Rectangle { Width = 20, Height = 20, RadiusX = 4, RadiusY = 4,
                    Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color.Hex)) } };
            button.Click += (_, _) => { SelectedColor = color.Hex; Complete(BatchMarkAction.Color); };
            ColorButtons.Children.Add(button);
            var item = new System.Windows.Controls.ComboBoxItem { Tag = color.Hex };
            var label = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            label.Children.Add(new System.Windows.Shapes.Rectangle { Width = 16, Height = 16, Margin = new Thickness(0,0,8,0),
                RadiusX = 3, RadiusY = 3, Fill = ((System.Windows.Shapes.Rectangle)button.Content).Fill });
            label.Children.Add(new System.Windows.Controls.TextBlock { Text = color.Name
                + (retainedColors?.Contains(color.Hex) == true ? "（必须保留）" : "") });
            item.Content = label;
            RetainedColorBox.Items.Add(item);
        }
        var clear = new System.Windows.Controls.Button { Content = "清除颜色" };
        clear.Click += (_, _) => Complete(BatchMarkAction.ClearColor);
        ColorButtons.Children.Add(clear);
        RetainedColorBox.SelectedIndex = 0;
    }

    private void Complete(BatchMarkAction action)
    {
        Action = action;
        DialogResult = true;
    }

    private void Retain_Click(object sender, RoutedEventArgs e) => CompleteColorRule(true);
    private void Unretain_Click(object sender, RoutedEventArgs e) => CompleteColorRule(false);
    private void CompleteColorRule(bool required)
    {
        if (RetainedColorBox.SelectedItem is not System.Windows.Controls.ComboBoxItem { Tag: string color }) return;
        SelectedColor = color;
        Complete(required ? BatchMarkAction.RetainColor : BatchMarkAction.UnretainColor);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
