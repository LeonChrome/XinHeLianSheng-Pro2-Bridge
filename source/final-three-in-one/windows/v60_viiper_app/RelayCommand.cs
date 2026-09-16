using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private readonly Predicate<object?>? canExecute;
    private bool running;

    public RelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        : this(parameter =>
        {
            execute(parameter);
            return Task.CompletedTask;
        }, canExecute)
    {
    }

    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            running = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            await execute(parameter);
        }
        finally
        {
            running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CanExecuteChanged;
}
