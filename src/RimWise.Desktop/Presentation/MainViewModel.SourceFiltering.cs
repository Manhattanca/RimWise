using RimWise.Core;

namespace RimWise.Desktop.Presentation;

public enum ModSourceFilter { All, Local, Workshop }

public sealed partial class MainViewModel
{
    public ModSourceFilter AvailableSourceFilter { get; private set; }
    public ModSourceFilter OrderSourceFilter { get; private set; }

    public void SetSourceFilter(ModSourceFilter filter, bool enabledList = false)
    {
        if (!Enum.IsDefined(filter)) throw new ArgumentOutOfRangeException(nameof(filter));
        if ((enabledList ? OrderSourceFilter : AvailableSourceFilter) == filter) return;
        var orderSelection = SelectedOrderRows.ToArray();
        var availableSelection = SelectedAvailableRows.ToArray();
        if (enabledList) { OrderSourceFilter = filter; VisibleRows.Refresh(); }
        else { AvailableSourceFilter = filter; VisibleAvailableMods.Refresh(); }
        // Hidden rows must not remain targets for a later batch operation.
        if (enabledList && orderSelection.Length > 0) SelectOrderRows(orderSelection.Where(VisibleRows.Contains).ToArray());
        if (!enabledList && availableSelection.Length > 0) SelectAvailableRows(availableSelection.Where(VisibleAvailableMods.Contains).ToArray());
        OnPropertyChanged(enabledList ? nameof(OrderSourceFilter) : nameof(AvailableSourceFilter));
        RefreshCounts();
        RefreshCommands();
    }

    private static bool MatchesSource(ModSource source, ModSourceFilter filter) => filter switch
    {
        ModSourceFilter.Local => source == ModSource.Local,
        ModSourceFilter.Workshop => source == ModSource.Workshop,
        _ => true
    };

    private bool MatchesSource(ModSource source) => MatchesSource(source, AvailableSourceFilter);

    private bool MatchesSource(LoadOrderRow row) => OrderSourceFilter == ModSourceFilter.All
        || row.Match?.Candidates.Any(mod => MatchesSource(mod.Source, OrderSourceFilter)) == true;
}
