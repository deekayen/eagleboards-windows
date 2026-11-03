using System.Windows.Input;

namespace EagleBoards.App;

/// <summary>
/// Wraps one action as an always-enabled command, because a WPF KeyBinding
/// (the main window's Ctrl+Z, F5, and F1) runs an <see cref="ICommand"/>, not a
/// method. <see cref="CanExecuteChanged"/> never fires, as nothing here can
/// change whether it may run.
/// </summary>
internal sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
