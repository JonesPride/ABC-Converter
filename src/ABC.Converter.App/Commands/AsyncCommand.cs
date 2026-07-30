using System.Windows.Input;

namespace ABC.Converter.App.Commands;

public sealed class AsyncCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? onException = null) : ICommand
{
    private int _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return Volatile.Read(ref _isExecuting) == 0 &&
            (canExecute?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync();
        }
        catch (Exception exception)
        {
            ReportException(exception);
        }
    }

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null) ||
            Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0)
        {
            return;
        }

        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            Volatile.Write(ref _isExecuting, 0);
            RaiseCanExecuteChanged();
        }
    }

    private void ReportException(Exception exception)
    {
        try
        {
            onException?.Invoke(exception);
        }
        catch (Exception handlerException)
        {
            System.Diagnostics.Trace.TraceError(
                "Command error handler failed: {0}",
                handlerException);
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
