namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private bool showModBadgeIcons = true;
    public bool ShowModBadgeIcons => showModBadgeIcons;
    public bool ShowModBadgeText => !showModBadgeIcons;
    public void SetModBadgeIcons(bool icons)
    {
        showModBadgeIcons = icons;
        OnPropertyChanged(nameof(ShowModBadgeIcons));
        OnPropertyChanged(nameof(ShowModBadgeText));
    }
}
