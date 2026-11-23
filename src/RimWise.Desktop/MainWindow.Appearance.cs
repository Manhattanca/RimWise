using RimWise.Desktop.Services;
using RimWise.Desktop.Views;

namespace RimWise.Desktop;

public partial class MainWindow
{
    internal AppearanceControlRegistry AppearanceControls { get; private set; } = null!;
    internal AppearanceControlPicker? OpacityPicker { get; private set; }
    private AppearanceWindow? appearanceEditor;
    internal async Task EditAppearanceAsync()
    {
        if (appearanceEditor is not null) { if (appearanceEditor.IsVisible) appearanceEditor.Activate(); return; }
        var editor = new AppearanceWindow { Owner = this }; appearanceEditor = editor;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                editor.ShowDialog();
                if (!editor.AdvancedRequested) return;
                // Hide exits the modal dispatcher frame and re-enables the owner without closing the draft.
                using (var picker = new AppearanceControlPicker(this, editor, AppearanceControls))
                {
                    OpacityPicker = picker; await picker.Completion;
                }
                OpacityPicker = null; editor.ResumeAdvancedEditing();
            }
        }
        finally { appearanceEditor = null; OpacityPicker?.Dispose(); OpacityPicker = null; }
    }
}
