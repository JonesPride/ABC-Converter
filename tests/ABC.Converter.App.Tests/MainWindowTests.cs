using System.Windows.Threading;
using ABC.Converter.App.Tests.TestSupport;
using ABC.Converter.App.ViewModels;
using Xunit;

namespace ABC.Converter.App.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public async Task ShowsTheProductionWindowWithoutXamlOrBindingErrors()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow
                {
                    DataContext = new MainWindowViewModel(ConversionHarness.CreateService()),
                };
                window.Show();
                window.Close();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                window?.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
