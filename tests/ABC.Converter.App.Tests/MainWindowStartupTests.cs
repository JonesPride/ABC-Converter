using System.Runtime.ExceptionServices;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Xunit;

namespace ABC.Converter.App.Tests;

public sealed class MainWindowStartupTests
{
    [Fact]
    public void MainWindowCanBeShownAndSettingsToggleCanBeOpened()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.Show();
                window.UpdateLayout();
                Assert.True(window.IsVisible);
                var toggle = Assert.IsType<ToggleButton>(window.FindName("SettingsToggle"));
                toggle.IsChecked = true;
                window.UpdateLayout();
                Assert.True(toggle.IsChecked);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                window?.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Window startup did not finish within 30 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
