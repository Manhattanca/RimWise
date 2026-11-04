using System.Windows.Input;

namespace RimWise.Desktop.Presentation;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter)
    {
        RimWise.Integration.Diagnostics.DiagnosticEventLog.Current?.Record("interface", "command-executed", execute.Method.Name);
        execute();
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
